// Zig's COFF driver roots this symbol even for a CRT-free DLL with no TLS.
// No thread_local storage or dynamic global constructors are used by this DLL.
extern "C" { unsigned long _tls_index = 0; }
