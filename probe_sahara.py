#!/usr/bin/env python3
import sys
import time
import usb.core
import usb.util

VID = 0x05C6
PID = 0x9008

def hexdump(data, width=16):
    for i in range(0, len(data), width):
        chunk = data[i:i+width]
        hexpart = " ".join(f"{b:02x}" for b in chunk)
        asc = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        print(f"{i:04x}  {hexpart:<48}  {asc}")

def main():
    print("===== FIND DEVICE =====")
    dev = usb.core.find(idVendor=VID, idProduct=PID)
    if dev is None:
        print("ERROR: Qualcomm 05c6:9008 device not found")
        sys.exit(1)

    print(f"PASS: Found device {VID:04x}:{PID:04x}")
    print(f"Bus    : {getattr(dev, 'bus', '?')}")
    print(f"Addr   : {getattr(dev, 'address', '?')}")

    try:
        dev.set_configuration()
    except usb.core.USBError as e:
        print(f"NOTE: set_configuration skipped/failed: {e}")

    cfg = dev.get_active_configuration()
    print(f"Config : {cfg.bConfigurationValue}")

    intf = None
    ep_in = None
    ep_out = None

    print("\n===== INTERFACES / ENDPOINTS =====")
    for interface in cfg:
        print(
            f"Interface {interface.bInterfaceNumber}, "
            f"Alt {interface.bAlternateSetting}, "
            f"Class 0x{interface.bInterfaceClass:02x}, "
            f"SubClass 0x{interface.bInterfaceSubClass:02x}, "
            f"Protocol 0x{interface.bInterfaceProtocol:02x}"
        )

        tmp_in = None
        tmp_out = None

        for ep in interface:
            direction = usb.util.endpoint_direction(ep.bEndpointAddress)
            dtype = usb.util.endpoint_type(ep.bmAttributes)

            dir_str = "IN" if direction == usb.util.ENDPOINT_IN else "OUT"
            type_str = {
                usb.util.ENDPOINT_TYPE_CTRL: "CTRL",
                usb.util.ENDPOINT_TYPE_ISO: "ISO",
                usb.util.ENDPOINT_TYPE_BULK: "BULK",
                usb.util.ENDPOINT_TYPE_INTR: "INTR",
            }.get(dtype, f"UNKNOWN({dtype})")

            print(
                f"  EP 0x{ep.bEndpointAddress:02x} "
                f"{dir_str:<3} {type_str:<4} "
                f"maxpkt={ep.wMaxPacketSize}"
            )

            if dtype == usb.util.ENDPOINT_TYPE_BULK:
                if direction == usb.util.ENDPOINT_IN and tmp_in is None:
                    tmp_in = ep
                elif direction == usb.util.ENDPOINT_OUT and tmp_out is None:
                    tmp_out = ep

        if tmp_in is not None and tmp_out is not None and intf is None:
            intf = interface
            ep_in = tmp_in
            ep_out = tmp_out

    if intf is None or ep_in is None or ep_out is None:
        print("ERROR: Could not find suitable bulk IN/OUT endpoints")
        sys.exit(2)

    print("\n===== SELECTED INTERFACE =====")
    print(f"Interface : {intf.bInterfaceNumber}")
    print(f"Bulk IN   : 0x{ep_in.bEndpointAddress:02x}")
    print(f"Bulk OUT  : 0x{ep_out.bEndpointAddress:02x}")

    if dev.is_kernel_driver_active(intf.bInterfaceNumber):
        print("Kernel driver active, detaching...")
        dev.detach_kernel_driver(intf.bInterfaceNumber)

    usb.util.claim_interface(dev, intf.bInterfaceNumber)

    try:
        print("\n===== READ INITIAL SAHARA DATA =====")
        try:
            data = dev.read(ep_in.bEndpointAddress, ep_in.wMaxPacketSize, timeout=3000)
            data = bytes(data)
            print(f"Read {len(data)} bytes")
            hexdump(data)
        except usb.core.USBError as e:
            print(f"NOTE: initial read timed out or failed: {e}")

        print("\n===== PROBE COMPLETE =====")
        print("This was read-only. No loader was sent.")
    finally:
        try:
            usb.util.release_interface(dev, intf.bInterfaceNumber)
        except Exception:
            pass
        usb.util.dispose_resources(dev)

if __name__ == "__main__":
    main()
