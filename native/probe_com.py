"""64-bit Windows COM smoke probe; no registry writes or audio service access.

Run in a separate process: python native/probe_com.py native/zsapo.dll
This checks the binary, not audiodg policy, formats, or the capture graph.
"""
import ctypes as c
import sys
import uuid
from pathlib import Path


def guid(value):
    return c.create_string_buffer(uuid.UUID(value).bytes_le)


def method(obj, index, result, *args):
    table = c.cast(obj, c.POINTER(c.c_void_p))[0]
    address = c.cast(table, c.POINTER(c.c_void_p))[index]
    return c.WINFUNCTYPE(result, c.c_void_p, *args)(address)


def check(label, hr, expected=0):
    print(f"{label}: 0x{hr & 0xffffffff:08X}", flush=True)
    if (hr & 0xffffffff) != expected:
        raise RuntimeError(label)


def release(obj):
    method(obj, 2, c.c_ulong)(obj)


if c.sizeof(c.c_void_p) != 8:
    raise RuntimeError("Use 64-bit Python")
path = Path(sys.argv[1] if len(sys.argv) > 1 else "native/zsapo.dll").resolve()
print(path, flush=True)
dll = c.WinDLL(str(path))
get = dll.DllGetClassObject
get.argtypes = [c.c_void_p] * 3
get.restype = c.c_long
unload = dll.DllCanUnloadNow
unload.restype = c.c_long
clsid = guid("5A7C3E91-2B4D-4F6A-9E1C-7D3B8A2F4C60")
factory_iid = guid("00000001-0000-0000-C000-000000000046")
unknown_iid = guid("00000000-0000-0000-C000-000000000046")
factory = c.c_void_p()
check("DllGetClassObject", get(clsid, factory_iid, c.byref(factory)))
try:
    # Older DLL incorrectly reports S_OK here despite a live factory reference.
    print(f"DllCanUnloadNow (live factory; expected S_FALSE): 0x{unload() & 0xffffffff:08X}", flush=True)
    create = method(factory, 3, c.c_long, c.c_void_p, c.c_void_p, c.c_void_p)
    obj = c.c_void_p()
    check("CreateInstance(IUnknown)", create(factory, None, unknown_iid, c.byref(obj)))
    try:
        query = method(obj, 0, c.c_long, c.c_void_p, c.c_void_p)
        for name, iid in [
            ("IAudioProcessingObject", "FD7F2B29-24D0-4B5C-B177-592C39F9CA10"),
            ("IAudioProcessingObjectRT", "9E1D6A6D-DDBC-4E95-A4C7-AD64BA37846C"),
            ("IAudioProcessingObjectConfiguration", "0E5ED805-ABA6-49C3-8F9A-2B8C889C4FA8"),
            ("IAudioSystemEffects", "5FA00F27-ADD6-499A-8A9D-6B98521FA75B"),
        ]:
            interface = c.c_void_p()
            check("QI " + name, query(obj, guid(iid), c.byref(interface)))
            try:
                identity = c.c_void_p()
                check("QI IUnknown", method(interface, 0, c.c_long, c.c_void_p, c.c_void_p)(interface, unknown_iid, c.byref(identity)))
                try:
                    if identity.value != obj.value:
                        raise RuntimeError("COM identity mismatch")
                finally:
                    release(identity)
            finally:
                release(interface)
        missing = c.c_void_p()
        check("QI unsupported", query(obj, guid("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"), c.byref(missing)), 0x80004002)
        if missing.value:
            raise RuntimeError("Failed QI must clear output")
    finally:
        release(obj)

    # Probe aggregation (audiodg wraps APOs inside its own outer object)
    outer_ref = [1]
    QI_FUNC = c.WINFUNCTYPE(c.c_long, c.c_void_p, c.c_void_p, c.c_void_p)
    ADDREF_FUNC = c.WINFUNCTYPE(c.c_ulong, c.c_void_p)
    RELEASE_FUNC = c.WINFUNCTYPE(c.c_ulong, c.c_void_p)

    def outer_qi(this, iid, out): return 0x80004002
    def outer_addref(this): outer_ref[0] += 1; return outer_ref[0]
    def outer_release(this): outer_ref[0] -= 1; return outer_ref[0]

    class Vtbl(c.Structure):
        _fields_ = [('qi', QI_FUNC), ('addref', ADDREF_FUNC), ('release', RELEASE_FUNC)]
    vtbl = Vtbl(QI_FUNC(outer_qi), ADDREF_FUNC(outer_addref), RELEASE_FUNC(outer_release))
    class MockOuter(c.Structure):
        _fields_ = [('lpVtbl', c.POINTER(Vtbl))]
    mock = MockOuter(c.pointer(vtbl))
    mock_ptr = c.cast(c.pointer(mock), c.c_void_p)

    rt_iid = guid("9E1D6A6D-DDBC-4E95-A4C7-AD64BA37846C")
    obj_fail = c.c_void_p()
    check("CreateInstance(outer, non-IUnknown expected 0x80040110)", create(factory, mock_ptr, rt_iid, c.byref(obj_fail)), 0x80040110)

    inner_unk = c.c_void_p()
    check("CreateInstance(outer, IUnknown)", create(factory, mock_ptr, unknown_iid, c.byref(inner_unk)))
    try:
        rt_obj = c.c_void_p()
        ref_before = outer_ref[0]
        check("inner QI RT", method(inner_unk, 0, c.c_long, c.c_void_p, c.c_void_p)(inner_unk, rt_iid, c.byref(rt_obj)))
        if outer_ref[0] != ref_before + 1:
            raise RuntimeError("inner QI must AddRef outer")
        release(rt_obj)
        if outer_ref[0] != ref_before:
            raise RuntimeError("RT release must Release outer")
    finally:
        release(inner_unk)
finally:
    release(factory)
check("DllCanUnloadNow (all released)", unload())
print("COM probe passed; real audiodg activation remains unverified.")
