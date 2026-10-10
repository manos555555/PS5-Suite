#!/usr/bin/env python3
"""FTP-stage + self-update + probe + run a trophy command on the PS5 suite."""
import socket, ftplib, sys, time, struct

IP = "192.168.0.53"
DIR = "/mnt/c/Users/HACKMAN/Desktop/ps5 test/my_projects/ps5_upload_suite/payload"
ELF = f"{DIR}/ps5_suite_server.elf"

CMD_SELF_UPDATE = 0x68
CMD_TROPHY_UNLOCK = 0x82
CMD_PING = 0x01

def rpc(cmd, payload=b"", timeout=20, port=None):
    for p in ([port] if port else range(9113, 9145)):
        try:
            s = socket.create_connection((IP, p), timeout=4)
            s.settimeout(timeout)
            s.sendall(bytes([cmd]) + struct.pack("<I", len(payload)) + payload)
            hdr = s.recv(5)
            if len(hdr) < 5:
                s.close(); continue
            ln = struct.unpack("<I", hdr[1:5])[0]
            data = b""
            while len(data) < ln:
                c = s.recv(min(65536, ln - len(data)))
                if not c: break
                data += c
            s.close()
            return p, hdr[0], data
        except OSError:
            continue
    return None, None, b""

def find_server():
    for p in range(9113, 9145):
        try:
            s = socket.create_connection((IP, p), timeout=2)
            s.settimeout(4)
            s.sendall(bytes([CMD_PING]) + struct.pack("<I", 0))
            hdr = s.recv(5)
            if len(hdr) == 5:
                ln = struct.unpack("<I", hdr[1:5])[0]
                data = s.recv(min(ln, 512)) if ln else b""
                s.close()
                return p, data
            s.close()
        except OSError:
            continue
    return None, b""

def ftp_put(local, remote):
    ftp = ftplib.FTP()
    ftp.connect(IP, 1337, timeout=15)
    try:
        ftp.login('anonymous', 'anonymous')
    except Exception:
        ftp.login('', '')
    with open(local, 'rb') as f:
        ftp.storbinary(f"STOR {remote}", f)
    ftp.quit()

if __name__ == "__main__":
    op = sys.argv[1] if len(sys.argv) > 1 else "full"

    if op in ("full", "deploy"):
        print("[1] ftp stage...")
        ftp_put(ELF, "/data/ps5suite/update.elf")
        print("    staged")
        print("[2] self-update cmd...")
        port, typ, d = rpc(CMD_SELF_UPDATE, b"/data/ps5suite/update.elf", timeout=30)
        print("    ->", port, hex(typ or 0), d[:200])
        if op == "deploy":
            sys.exit(0)
        print("[3] wait for new server...")
        time.sleep(5)

    if op in ("full", "probe"):
        for i in range(6):
            p, d = find_server()
            if p:
                print("    ALIVE on", p, "->", d[:80])
                SRV = p
                break
            time.sleep(4)
        else:
            print("    NO SERVER"); sys.exit(1)

    if op == "full" and len(sys.argv) > 2:
        arg = sys.argv[2].encode()
        p, typ, d = rpc(CMD_TROPHY_UNLOCK, arg, timeout=90, port=SRV)
        print("[cmd]", arg.decode(), "->", hex(typ or 0), d[:1500])
    elif op == "cmd":
        p, _ = find_server()
        arg = " ".join(sys.argv[2:]).encode()
        p, typ, d = rpc(CMD_TROPHY_UNLOCK, arg, timeout=90, port=p)
        print("->", hex(typ or 0), d[:1500])
