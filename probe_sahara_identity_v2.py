#!/usr/bin/env python3

import struct
import sys
import usb.core
import usb.util

VID = 0x05C6
PID = 0x9008

HELLO         = 0x01
HELLO_RESP    = 0x02
COMMAND_READY = 0x0B
SWITCH_MODE   = 0x0C
EXECUTE       = 0x0D
EXECUTE_RESP  = 0x0E
EXECUTE_DATA  = 0x0F

MODE_WAITING = 0
MODE_COMMAND = 3

READ_SERIAL = 1
READ_HWID   = 2
READ_PKHash = 3


def hexdump(data):
    for off in range(0, len(data), 16):
        row = data[off:off+16]
        hx = " ".join(f"{b:02x}" for b in row)
        asc = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
        print(f"{off:04x}  {hx:<48}  {asc}")


def recv(ep, size=4096, timeout=5000):
    return bytes(ep.read(size, timeout=timeout))


def send(ep, data):
    n = ep.write(data, timeout=5000)
    if n != len(data):
        raise RuntimeError(f"Short write {n}/{len(data)}")


def header(data):
    if len(data) < 8:
        raise RuntimeError(f"Short Sahara packet: {len(data)}")
    return struct.unpack_from("<II", data, 0)


def hello_response():
    return struct.pack(
        "<12I",
        HELLO_RESP,
        48,
        2,              # host Sahara version
        1,
        0,              # status success
        MODE_COMMAND,
        0, 0, 0, 0, 0, 0
    )


def execute(command):
    return struct.pack("<III", EXECUTE, 12, command)


def execute_data(command):
    return struct.pack("<III", EXECUTE_DATA, 12, command)


def switch_waiting():
    return struct.pack("<III", SWITCH_MODE, 12, MODE_WAITING)


def wait_for_hello(ep_in):
    print("Waiting for HELLO...")

    pkt = recv(ep_in, 512, 7000)
    cmd, length = header(pkt)

    if cmd != HELLO:
        print(f"Unexpected packet cmd=0x{cmd:x} len={length}")
        hexdump(pkt)
        raise RuntimeError("Expected SAHARA_HELLO")

    if len(pkt) >= 24:
        _, _, ver, compat, maxlen, mode = struct.unpack_from(
            "<6I", pkt, 0
        )

        print(
            f"HELLO: version={ver} compatible={compat} "
            f"maxlen={maxlen} mode={mode}"
        )

    return pkt


def enter_command_mode(ep_in, ep_out):
    wait_for_hello(ep_in)

    send(ep_out, hello_response())

    pkt = recv(ep_in, 512)
    cmd, length = header(pkt)

    if cmd != COMMAND_READY:
        print(
            f"Expected COMMAND_READY, "
            f"got cmd=0x{cmd:x} len={length}"
        )
        hexdump(pkt)
        raise RuntimeError("Failed to enter command mode")

    print("PASS: command mode ready")


def query_one(ep_in, ep_out, command, name):
    print()
    print(f"===== {name} =====")

    enter_command_mode(ep_in, ep_out)

    send(ep_out, execute(command))

    pkt = recv(ep_in, 512)
    cmd, length = header(pkt)

    print(
        f"EXEC response: cmd=0x{cmd:02x} "
        f"length={length}"
    )

    if cmd != EXECUTE_RESP:
        print("Unexpected response:")
        hexdump(pkt)
        raise RuntimeError(
            f"Expected EXECUTE_RESP, got 0x{cmd:x}"
        )

    if len(pkt) < 16:
        raise RuntimeError("EXECUTE_RESP too short")

    returned_cmd, data_len = struct.unpack_from(
        "<II", pkt, 8
    )

    print(f"Command     : 0x{returned_cmd:08x}")
    print(f"Data length : {data_len}")

    if returned_cmd != command:
        raise RuntimeError(
            f"Command mismatch {returned_cmd} != {command}"
        )

    send(ep_out, execute_data(command))

    data = b""

    while len(data) < data_len:
        chunk = recv(
            ep_in,
            max(512, data_len - len(data))
        )
        data += chunk

    data = data[:data_len]

    print(f"Raw ({len(data)} bytes):")
    hexdump(data)

    #
    # Qualcomm qdl exits command mode after each command.
    #
    print("Returning Sahara to WaitingForImage...")
    send(ep_out, switch_waiting())

    return data


def main():
    print("==============================================")
    print("  QUALCOMM SAHARA IDENTITY PROBE v2")
    print("==============================================")
    print("Read-only Sahara identity commands only.")
    print("No programmer. No Firehose. No storage access.")
    print()

    dev = usb.core.find(
        idVendor=VID,
        idProduct=PID
    )

    if dev is None:
        sys.exit("ERROR: 05c6:9008 not found")

    print(f"Device  : {VID:04x}:{PID:04x}")
    print(f"Bus     : {getattr(dev, 'bus', '?')}")
    print(f"Address : {getattr(dev, 'address', '?')}")

    try:
        dev.set_configuration()
    except usb.core.USBError:
        pass

    cfg = dev.get_active_configuration()

    intf = None
    ep_in = None
    ep_out = None

    for candidate in cfg:
        cin = None
        cout = None

        for ep in candidate:
            if (
                usb.util.endpoint_type(ep.bmAttributes)
                != usb.util.ENDPOINT_TYPE_BULK
            ):
                continue

            direction = usb.util.endpoint_direction(
                ep.bEndpointAddress
            )

            if direction == usb.util.ENDPOINT_IN:
                cin = ep
            else:
                cout = ep

        if cin is not None and cout is not None:
            intf = candidate
            ep_in = cin
            ep_out = cout
            break

    if intf is None:
        sys.exit("ERROR: Sahara bulk interface not found")

    print(f"Interface: {intf.bInterfaceNumber}")
    print(f"IN       : 0x{ep_in.bEndpointAddress:02x}")
    print(f"OUT      : 0x{ep_out.bEndpointAddress:02x}")

    detached = False

    try:
        if dev.is_kernel_driver_active(
            intf.bInterfaceNumber
        ):
            dev.detach_kernel_driver(
                intf.bInterfaceNumber
            )
            detached = True
    except Exception:
        pass

    usb.util.claim_interface(
        dev,
        intf.bInterfaceNumber
    )

    try:
        serial = query_one(
            ep_in,
            ep_out,
            READ_SERIAL,
            "SERIAL NUMBER"
        )

        hwid = query_one(
            ep_in,
            ep_out,
            READ_HWID,
            "HARDWARE ID"
        )

        pkhash = query_one(
            ep_in,
            ep_out,
            READ_PKHash,
            "OEM KEY HASH"
        )

        print()
        print("==============================================")
        print("               IDENTITY SUMMARY")
        print("==============================================")

        print(f"Serial response : {serial.hex()}")

        if len(serial) >= 4:
            sn = struct.unpack_from("<I", serial, 0)[0]
            print(f"Chip serial     : 0x{sn:08x}")

        print(f"HWID response   : {hwid.hex()}")

        if len(hwid) >= 8:
            print(
                "HWID LE         : "
                f"0x{int.from_bytes(hwid[:8], 'little'):016x}"
            )

        print(f"PKHASH response : {pkhash.hex()}")

        print()
        print("PASS: all Sahara identity commands completed")
        print("Device left in WaitingForImage mode.")
        print("No programmer was uploaded.")

    finally:
        try:
            usb.util.release_interface(
                dev,
                intf.bInterfaceNumber
            )
        except Exception:
            pass

        if detached:
            try:
                dev.attach_kernel_driver(
                    intf.bInterfaceNumber
                )
            except Exception:
                pass

        usb.util.dispose_resources(dev)


if __name__ == "__main__":
    main()
