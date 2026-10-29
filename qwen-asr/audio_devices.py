"""Read the live Windows default output ID without PortAudio's device-list cache."""
import ctypes as ct
import uuid


class GUID(ct.Structure):
    _fields_ = [('data1', ct.c_uint32), ('data2', ct.c_uint16),
                ('data3', ct.c_uint16), ('data4', ct.c_ubyte * 8)]


def _guid(value):
    return GUID.from_buffer_copy(uuid.UUID(value).bytes_le)


def _method(pointer, index, result, *arguments):
    table = ct.cast(pointer, ct.POINTER(ct.POINTER(ct.c_void_p))).contents
    return ct.WINFUNCTYPE(result, ct.c_void_p, *arguments)(table[index])


def _check(result):
    if result < 0:
        raise OSError(f'Windows audio endpoint query failed: 0x{result & 0xffffffff:08X}')


def get_default_output_id():
    # IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eMultimedia),
    # matching the default output role used by PyAudioWPatch's WASAPI backend.
    # IMMDevice::GetId returns a CoTaskMemAlloc string, which we free below.
    ole = ct.WinDLL('ole32')
    ole.CoInitializeEx.argtypes = [ct.c_void_p, ct.c_uint32]
    ole.CoInitializeEx.restype = ct.c_long
    ole.CoUninitialize.argtypes = []
    ole.CoUninitialize.restype = None
    ole.CoCreateInstance.argtypes = [ct.POINTER(GUID), ct.c_void_p, ct.c_uint32,
                                    ct.POINTER(GUID), ct.POINTER(ct.c_void_p)]
    ole.CoCreateInstance.restype = ct.c_long
    ole.CoTaskMemFree.argtypes = [ct.c_void_p]
    ole.CoTaskMemFree.restype = None

    initialized = ole.CoInitializeEx(None, 0)
    if initialized < 0 and (initialized & 0xffffffff) != 0x80010106:  # RPC_E_CHANGED_MODE
        _check(initialized)
    enumerator, device, identifier = ct.c_void_p(), ct.c_void_p(), ct.c_void_p()
    try:
        clsid = _guid('BCDE0395-E52F-467C-8E3D-C4579291692E')
        iid = _guid('A95664D2-9614-4F35-A746-DE8DB63617E6')
        _check(ole.CoCreateInstance(ct.byref(clsid), None, 1, ct.byref(iid), ct.byref(enumerator)))
        result = _method(enumerator, 4, ct.c_long, ct.c_int, ct.c_int,
                         ct.POINTER(ct.c_void_p))(enumerator, 0, 1, ct.byref(device))
        if (result & 0xffffffff) == 0x80070490:  # E_NOTFOUND: all outputs disconnected
            return None
        _check(result)
        _check(_method(device, 5, ct.c_long, ct.POINTER(ct.c_void_p))(device, ct.byref(identifier)))
        return ct.wstring_at(identifier)
    finally:
        if identifier:
            ole.CoTaskMemFree(identifier)
        if device:
            _method(device, 2, ct.c_ulong)(device)
        if enumerator:
            _method(enumerator, 2, ct.c_ulong)(enumerator)
        if initialized >= 0:
            ole.CoUninitialize()
