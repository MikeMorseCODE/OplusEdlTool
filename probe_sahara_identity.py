#!/usr/bin/env python3

import struct
import sys
import usb.core
import usb.util

VID = 0x05C6
PID = 0x9008

SAHARA_HELLO         = 0x01
SAHARA_HELLO_RESP    = 0x02
SAHARA_RESET         = 0x07
SAHARA_RESET_RESP    = 0x08
SAHARA_COMMAND_READY = 0x0B
SAHARA_EXECUTE       = 0x0D
SAHARA_EXECUTE_RESP  = 0x0E
SAHARA_EXECUTE_DATA  = 0x0F

MODE_COMMAND = 0x03

CMD_SERIAL_NUM    = 0x01
CMD_HWID          = 0x02
CMD_OEM_KEY_HASH  = 0x03


def hd(data):
    for off in range(0, len(data), 16):
        row = data[off:off+16]
        hx = " ".join(f"{x:02x}" for x in row)
        asc = "".join(chr(x) if 32 <= x < 127 else "." for x in row)
        print(f"{off:04x}  {hx:<48}  {asc}")


def read_usb(ep, size=4096, timeout=5000):
    return bytes(ep.read(size, timeout=timeout))


def packet_info(data):
    if len(data) < 8:
        raise RuntimeError(f"Short Sahara packet: {len(data)} bytes")

    cmd, length = struct.unpack_from("<II", data, 0)
    return cmd, length


def send(ep, data):
    written = ep.write(data, timeout=5000)
    if written != len(data):
        raise RuntimeError(
            f"Short USB write: {written}/{len(data)}"
        )


def hello_response():
    # Header:
    #   command = HELLO_RESP
    #   length  = 48
    #
    # Body:
    #   version       = 2
    #   compatible    = 1
    #   status        = 0
    #   mode          = COMMAND (3)
    #   reserved[6]   = 0
    return struct.pack(
        "<12I",
        SAHARA_HELLO_RESP,
        48,
        2,
        1,
        0,
        MODE_COMMAND,
        0, 0, 0, 0, 0, 0
    )


def execute_request(command):
    return struct.pack(
        "<III",
        SAHARA_EXECUTE,
        12,
        command
    )


def execute_data_request(command):
    return struct.pack(
        "<III",
        SAHARA_EXECUTE_DATA,
        12,
        command
    )


def reset_request():
    return struct.pack(
        "<II",
        SAHARA_RESET,
        8
    )


def query_command(ep_in, ep_out, command, name):
    print()
    print(f"===== {name} =====")

    send(ep_out, execute_request(command))

    resp = read_usb(ep_in)

    cmd, length = packet_info(resp)

    print(
        f"EXEC response: cmd=0x{cmd:02x} "
        f"length={length}"
    )

    if cmd != SAHARA_EXECUTE_RESP:
        print("Unexpected response:")
        hd(resp)
        raise RuntimeError(
            f"Expected EXECUTE_RESP 0x{SAHARA_EXECUTE_RESP:x}, "
            f"got 0x{cmd:x}"
        )

    if len(resp) < 16:
        raise RuntimeError(
            f"EXECUTE_RESP too short: {len(resp)}"
        )

    returned_command, data_length = struct.unpack_from(
        "<II", resp, 8
    )

    print(f"Command     : 0x{returned_command:08x}")
    print(f"Data length : {data_length}")

    if returned_command != command:
        raise RuntimeError(
            f"Command mismatch: requested {command}, "
            f"device returned {returned_command}"
        )

    send(ep_out, execute_data_request(command))

    if data_length == 0:
        print("No data returned.")
        return b""

    data = read_usb(
        ep_in,
        max(data_length, 512)
    )

    if len(data) < data_length:
        # Extremely unlikely for these tiny identity queries,
        # but handle a split USB transfer.
        remaining = data_length - len(data)

        while remaining:
            chunk = read_usb(
                ep_in,
                max(remaining, 512)
            )
            data += chunk
            remaining = data_length - len(data)

    data = data[:data_length]

    print(f"Raw ({len(data)} bytes):")
    hd(data)

    return data


def main():
    print("===============================================")
    print("     OPLUS / QUALCOMM SAHARA IDENTITY PROBE")
    print("===============================================")
    print()
    print("No programmer will be uploaded.")
    print("No Firehose commands will be issued.")
    print("No device storage will be accessed.")
    print()

    print("===== FIND 9008 DEVICE =====")

    dev = usb.core.find(
        idVendor=VID,
        idProduct=PID
    )

    if dev is None:
        print("ERROR: 05c6:9008 not found")
        sys.exit(1)

    print(f"PASS: {VID:04x}:{PID:04x}")
    print(f"Bus     : {getattr(dev, 'bus', '?')}")
    print(f"Address : {getattr(dev, 'address', '?')}")

    try:
        dev.set_configuration()
    except usb.core.USBError as e:
        print(f"set_configuration: {e}")

    cfg = dev.get_active_configuration()

    interface = None
    ep_in = None
    ep_out = None

    for intf in cfg:
        in_candidate = None
        out_candidate = None

        for ep in intf:
            if (
                usb.util.endpoint_type(ep.bmAttributes)
                != usb.util.ENDPOINT_TYPE_BULK
            ):
                continue

            direction = usb.util.endpoint_direction(
                ep.bEndpointAddress
            )

            if direction == usb.util.ENDPOINT_IN:
                in_candidate = ep
            else:
                out_candidate = ep

        if (
            in_candidate is not None
            and out_candidate is not None
        ):
            interface = intf
            ep_in = in_candidate
            ep_out = out_candidate
            break

    if interface is None:
        raise RuntimeError(
            "No BULK IN/OUT Sahara interface found"
        )

    print()
    print("===== USB TRANSPORT =====")
    print(
        f"Interface : {interface.bInterfaceNumber}"
    )
    print(
        f"IN        : 0x{ep_in.bEndpointAddress:02x}"
    )
    print(
        f"OUT       : 0x{ep_out.bEndpointAddress:02x}"
    )

    detached = False

    try:
        if dev.is_kernel_driver_active(
            interface.bInterfaceNumber
        ):
            dev.detach_kernel_driver(
                interface.bInterfaceNumber
            )
            detached = True
    except (NotImplementedError, usb.core.USBError):
        pass

    usb.util.claim_interface(
        dev,
        interface.bInterfaceNumber
    )

    try:
        print()
        print("===== WAIT FOR SAHARA HELLO =====")

        try:
            hello = read_usb(
                ep_in,
                512,
                timeout=6000
            )
        except usb.core.USBError as e:
            print(f"ERROR waiting for HELLO: {e}")
            print()
            print(
                "If the previous probe consumed the HELLO, "
                "disconnect/re-enter 9008 and rerun."
            )
            sys.exit(2)

        cmd, length = packet_info(hello)

        print(
            f"Received cmd=0x{cmd:02x}, "
            f"length={length}"
        )

        if cmd != SAHARA_HELLO:
            print("Unexpected packet:")
            hd(hello)
            raise RuntimeError(
                "Device did not send SAHARA_HELLO"
            )

        if len(hello) >= 24:
            (
                _cmd,
                _length,
                version,
                compatible,
                max_len,
                mode
            ) = struct.unpack_from("<6I", hello, 0)

            print(f"Version       : {version}")
            print(f"Compatible    : {compatible}")
            print(f"Max cmd length: {max_len}")
            print(f"Current mode  : {mode}")

        print()
        print("===== ENTER COMMAND MODE =====")

        send(ep_out, hello_response())

        ready = read_usb(ep_in, 512)

        rcmd, rlen = packet_info(ready)

        print(
            f"Response: cmd=0x{rcmd:02x}, "
            f"length={rlen}"
        )

        if rcmd != SAHARA_COMMAND_READY:
            print("Unexpected response:")
            hd(ready)

            raise RuntimeError(
                "Expected SAHARA_COMMAND_READY"
            )

        print("PASS: Sahara command mode ready")

        serial = query_command(
            ep_in,
            ep_out,
            CMD_SERIAL_NUM,
            "SERIAL NUMBER"
        )

        hwid = query_command(
            ep_in,
            ep_out,
            CMD_HWID,
            "HARDWARE ID"
        )

        pkhash = query_command(
            ep_in,
            ep_out,
            CMD_OEM_KEY_HASH,
            "OEM KEY HASH"
        )

        print()
        print("===============================================")
        print("                IDENTITY SUMMARY")
        print("===============================================")

        print(
            "Serial raw : "
            + serial.hex()
        )

        if len(serial) <= 8 and serial:
            print(
                "Serial LE  : "
                + str(
                    int.from_bytes(
                        serial,
                        "little"
                    )
                )
            )
            print(
                "Serial hex : "
                + f"0x{int.from_bytes(serial, 'little'):x}"
            )

        print(
            "HWID raw   : "
            + hwid.hex()
        )

        if len(hwid) <= 16 and hwid:
            print(
                "HWID LE    : "
                + f"0x{int.from_bytes(hwid, 'little'):x}"
            )

        print(
            "OEM PKHASH : "
            + pkhash.hex()
        )

        print()
        print("===== RESET SAHARA SESSION =====")

        try:
            send(ep_out, reset_request())

            reset_resp = read_usb(
                ep_in,
                512,
                timeout=3000
            )

            c, l = packet_info(reset_resp)

            if c == SAHARA_RESET_RESP:
                print(
                    "PASS: device acknowledged Sahara reset"
                )
            else:
                print(
                    f"Reset returned cmd=0x{c:x}, len={l}"
                )

        except usb.core.USBError:
            print(
                "Device disconnected/reinitialized during reset."
            )

        print()
        print("===== COMPLETE =====")
        print(
            "No programmer or Firehose payload was sent."
        )

    finally:
        try:
            usb.util.release_interface(
                dev,
                interface.bInterfaceNumber
            )
        except Exception:
            pass

        if detached:
            try:
                dev.attach_kernel_driver(
                    interface.bInterfaceNumber
                )
            except Exception:
                pass

        usb.util.dispose_resources(dev)


if __name__ == "__main__":
    main()
