

using System.IO;
using System.Runtime.InteropServices;
using System;
namespace uniffi.didcomm;
using FfiConverterTypeJsonValue = FfiConverterString;
using JsonValue = String;



// This is a helper for safely working with byte buffers returned from the Rust code.
// A rust-owned buffer is represented by its capacity, its current length, and a
// pointer to the underlying data.

[StructLayout(LayoutKind.Sequential)]
internal struct RustBuffer {
    public int capacity;
    public int len;
    public IntPtr data;

    public static RustBuffer Alloc(int size) {
        return _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            var buffer = _UniFFILib.ffi_didcomm_f8e5_rustbuffer_alloc(size, ref status);
            if (buffer.data == IntPtr.Zero) {
                throw new AllocationException($"RustBuffer.Alloc() returned null data pointer (size={size})");
            }
            return buffer;
        });
    }

    public static void Free(RustBuffer buffer) {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_rustbuffer_free(buffer, ref status);
        });
    }

    public static BigEndianStream MemoryStream(IntPtr data, int length) {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), length));
        }
    }

    public BigEndianStream AsStream() {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), len));
        }
    }

    public BigEndianStream AsWriteableStream() {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), capacity, capacity, FileAccess.Write));
        }
    }
}

// This is a helper for safely passing byte references into the rust code.
// It's not actually used at the moment, because there aren't many things that you
// can take a direct pointer to managed memory, and if we're going to copy something
// then we might as well copy it into a `RustBuffer`. But it's here for API
// completeness.

[StructLayout(LayoutKind.Sequential)]
internal struct ForeignBytes {
    public int length;
    public IntPtr data;
}


// The FfiConverter interface handles converter types to and from the FFI
//
// All implementing objects should be public to support external types.  When a
// type is external we need to import it's FfiConverter.
internal abstract class FfiConverter<CsType, FfiType> {
    // Convert an FFI type to a C# type
    public abstract CsType Lift(FfiType value);

    // Convert C# type to an FFI type
    public abstract FfiType Lower(CsType value);

    // Read a C# type from a `ByteBuffer`
    public abstract CsType Read(BigEndianStream stream);

    // Calculate bytes to allocate when creating a `RustBuffer`
    //
    // This must return at least as many bytes as the write() function will
    // write. It can return more bytes than needed, for example when writing
    // Strings we can't know the exact bytes needed until we the UTF-8
    // encoding, so we pessimistically allocate the largest size possible (3
    // bytes per codepoint).  Allocating extra bytes is not really a big deal
    // because the `RustBuffer` is short-lived.
    public abstract int AllocationSize(CsType value);

    // Write a C# type to a `ByteBuffer`
    public abstract void Write(CsType value, BigEndianStream stream);

    // Lower a value into a `RustBuffer`
    //
    // This method lowers a value into a `RustBuffer` rather than the normal
    // FfiType.  It's used by the callback interface code.  Callback interface
    // returns are always serialized into a `RustBuffer` regardless of their
    // normal FFI type.
    public RustBuffer LowerIntoRustBuffer(CsType value) {
        var rbuf = RustBuffer.Alloc(AllocationSize(value));
        try {
            var stream = rbuf.AsWriteableStream();
            Write(value, stream);
            rbuf.len = Convert.ToInt32(stream.Position);
            return rbuf;
        } catch {
            RustBuffer.Free(rbuf);
            throw;
        }
    }

    // Lift a value from a `RustBuffer`.
    //
    // This here mostly because of the symmetry with `lowerIntoRustBuffer()`.
    // It's currently only used by the `FfiConverterRustBuffer` class below.
    protected CsType LiftFromRustBuffer(RustBuffer rbuf) {
        var stream = rbuf.AsStream();
        try {
           var item = Read(stream);
           if (stream.HasRemaining()) {
               throw new InternalException("junk remaining in buffer after lifting, something is very wrong!!");
           }
           return item;
        } finally {
            RustBuffer.Free(rbuf);
        }
    }
}

// FfiConverter that uses `RustBuffer` as the FfiType
internal abstract class FfiConverterRustBuffer<CsType>: FfiConverter<CsType, RustBuffer> {
    public override CsType Lift(RustBuffer value) {
        return LiftFromRustBuffer(value);
    }
    public override RustBuffer Lower(CsType value) {
        return LowerIntoRustBuffer(value);
    }
}


// A handful of classes and functions to support the generated data structures.
// This would be a good candidate for isolating in its own ffi-support lib.
// Error runtime.
[StructLayout(LayoutKind.Sequential)]
struct RustCallStatus {
    public int code;
    public RustBuffer error_buf;

    public bool IsSuccess() {
        return code == 0;
    }

    public bool IsError() {
        return code == 1;
    }

    public bool IsPanic() {
        return code == 2;
    }
}

// Base class for all uniffi exceptions
public class UniffiException: Exception {
    public UniffiException(): base() {}
    public UniffiException(string message): base(message) {}
}

public class UndeclaredErrorException: UniffiException {
    public UndeclaredErrorException(string message): base(message) {}
}

public class PanicException: UniffiException {
    public PanicException(string message): base(message) {}
}

public class AllocationException: UniffiException {
    public AllocationException(string message): base(message) {}
}

public class InternalException: UniffiException {
    public InternalException(string message): base(message) {}
}

public class InvalidEnumException: InternalException {
    public InvalidEnumException(string message): base(message) {
    }
}

// Each top-level error class has a companion object that can lift the error from the call status's rust buffer
interface CallStatusErrorHandler<E> where E: Exception {
    E Lift(RustBuffer error_buf);
}

// CallStatusErrorHandler implementation for times when we don't expect a CALL_ERROR
class NullCallStatusErrorHandler: CallStatusErrorHandler<UniffiException> {
    public static NullCallStatusErrorHandler INSTANCE = new NullCallStatusErrorHandler();

    public UniffiException Lift(RustBuffer error_buf) {
        RustBuffer.Free(error_buf);
        return new UndeclaredErrorException("library has returned an error not declared in UNIFFI interface file");
    }
}

// Helpers for calling Rust
// In practice we usually need to be synchronized to call this safely, so it doesn't
// synchronize itself
class _UniffiHelpers {
    public delegate void RustCallAction(ref RustCallStatus status);
    public delegate U RustCallFunc<out U>(ref RustCallStatus status);

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static U RustCallWithError<U, E>(CallStatusErrorHandler<E> errorHandler, RustCallFunc<U> callback)
        where E: UniffiException
    {
        var status = new RustCallStatus();
        var return_value = callback(ref status);
        if (status.IsSuccess()) {
            return return_value;
        } else if (status.IsError()) {
            throw errorHandler.Lift(status.error_buf);
        } else if (status.IsPanic()) {
            // when the rust code sees a panic, it tries to construct a rustbuffer
            // with the message.  but if that code panics, then it just sends back
            // an empty buffer.
            if (status.error_buf.len > 0) {
                throw new PanicException(FfiConverterString.INSTANCE.Lift(status.error_buf));
            } else {
                throw new PanicException("Rust panic");
            }
        } else {
            throw new InternalException($"Unknown rust call status: {status.code}");
        }
    }

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static void RustCallWithError<E>(CallStatusErrorHandler<E> errorHandler, RustCallAction callback)
        where E: UniffiException
    {
        _UniffiHelpers.RustCallWithError(errorHandler, (ref RustCallStatus status) => {
            callback(ref status);
            return 0;
        });
    }

    // Call a rust function that returns a plain value
    public static U RustCall<U>(RustCallFunc<U> callback) {
        return _UniffiHelpers.RustCallWithError(NullCallStatusErrorHandler.INSTANCE, callback);
    }

    // Call a rust function that returns a plain value
    public static void RustCall(RustCallAction callback) {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            callback(ref status);
            return 0;
        });
    }
}


// Big endian streams are not yet available in dotnet :'(
// https://github.com/dotnet/runtime/issues/26904

class StreamUnderflowException: Exception {
    public StreamUnderflowException() {
    }
}

class BigEndianStream {
    Stream stream;
    public BigEndianStream(Stream stream) {
        this.stream = stream;
    }

    public bool HasRemaining() {
        return (stream.Length - stream.Position) > 0;
    }

    public long Position {
        get => stream.Position;
        set => stream.Position = value;
    }

    public void WriteBytes(byte[] value) {
        stream.Write(value, 0, value.Length);
    }

    public void WriteByte(byte value) {
        stream.WriteByte(value);
    }

    public void WriteUShort(ushort value) {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteUInt(uint value) {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteULong(ulong value) {
        WriteUInt((uint)(value >> 32));
        WriteUInt((uint)value);
    }

    public void WriteSByte(sbyte value) {
        stream.WriteByte((byte)value);
    }

    public void WriteShort(short value) {
        WriteUShort((ushort)value);
    }

    public void WriteInt(int value) {
        WriteUInt((uint)value);
    }

    public void WriteFloat(float value) {
        WriteInt(BitConverter.SingleToInt32Bits(value));
    }

    public void WriteLong(long value) {
        WriteULong((ulong)value);
    }

    public void WriteDouble(double value) {
        WriteLong(BitConverter.DoubleToInt64Bits(value));
    }

    public byte[] ReadBytes(int length) {
        CheckRemaining(length);
        byte[] result = new byte[length];
        stream.Read(result, 0, length);
        return result;
    }

    public byte ReadByte() {
        CheckRemaining(1);
        return Convert.ToByte(stream.ReadByte());
    }

    public ushort ReadUShort() {
        CheckRemaining(2);
        return (ushort)(stream.ReadByte() << 8 | stream.ReadByte());
    }

    public uint ReadUInt() {
        CheckRemaining(4);
        return (uint)(stream.ReadByte() << 24
            | stream.ReadByte() << 16
            | stream.ReadByte() << 8
            | stream.ReadByte());
    }

    public ulong ReadULong() {
        return (ulong)ReadUInt() << 32 | (ulong)ReadUInt();
    }

    public sbyte ReadSByte() {
        return (sbyte)ReadByte();
    }

    public short ReadShort() {
        return (short)ReadUShort();
    }

    public int ReadInt() {
        return (int)ReadUInt();
    }

    public float ReadFloat() {
        return BitConverter.Int32BitsToSingle(ReadInt());
    }

    public long ReadLong() {
        return (long)ReadULong();
    }

    public double ReadDouble() {
        return BitConverter.Int64BitsToDouble(ReadLong());
    }

    private void CheckRemaining(int length) {
        if (stream.Length - stream.Position < length) {
            throw new StreamUnderflowException();
        }
    }
}

// Contains loading, initialization code,
// and the FFI Function declarations in a com.sun.jna.Library.


// This is an implementation detail which will be called internally by the public API.
static class _UniFFILib {
    static _UniFFILib() {
        
        FfiConverterTypeDidResolver.INSTANCE.Register();
        FfiConverterTypeOnFromPriorPackResult.INSTANCE.Register();
        FfiConverterTypeOnFromPriorUnpackResult.INSTANCE.Register();
        FfiConverterTypeOnPackEncryptedResult.INSTANCE.Register();
        FfiConverterTypeOnPackPlaintextResult.INSTANCE.Register();
        FfiConverterTypeOnPackSignedResult.INSTANCE.Register();
        FfiConverterTypeOnUnpackResult.INSTANCE.Register();
        FfiConverterTypeOnWrapInForwardResult.INSTANCE.Register();
        FfiConverterTypeSecretsResolver.INSTANCE.Register();
        }

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_DIDComm_object_free(IntPtr @ptr,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern DIDCommSafeHandle didcomm_f8e5_DIDComm_new(ulong @didResolver,ulong @secretResolver,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_DIDComm_pack_plaintext(DIDCommSafeHandle @ptr,RustBuffer @msg,ulong @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_DIDComm_pack_signed(DIDCommSafeHandle @ptr,RustBuffer @msg,RustBuffer @signBy,ulong @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_DIDComm_pack_encrypted(DIDCommSafeHandle @ptr,RustBuffer @msg,RustBuffer @to,RustBuffer @from,RustBuffer @signBy,RustBuffer @options,ulong @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_DIDComm_unpack(DIDCommSafeHandle @ptr,RustBuffer @msg,RustBuffer @options,ulong @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_DIDComm_pack_from_prior(DIDCommSafeHandle @ptr,RustBuffer @msg,RustBuffer @issuerKid,ulong @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_DIDComm_unpack_from_prior(DIDCommSafeHandle @ptr,RustBuffer @fromPriorJwt,ulong @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_DIDComm_wrap_in_forward(DIDCommSafeHandle @ptr,RustBuffer @msg,RustBuffer @headers,RustBuffer @to,RustBuffer @routingKeys,RustBuffer @encAlgAnon,ulong @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnDIDResolverResult_object_free(IntPtr @ptr,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void didcomm_f8e5_OnDIDResolverResult_success(OnDIDResolverResultSafeHandle @ptr,RustBuffer @result,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void didcomm_f8e5_OnDIDResolverResult_error(OnDIDResolverResultSafeHandle @ptr,RustBuffer @err,RustBuffer @msg,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_ExampleDIDResolver_object_free(IntPtr @ptr,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern ExampleDIDResolverSafeHandle didcomm_f8e5_ExampleDIDResolver_new(RustBuffer @knownDids,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_ExampleDIDResolver_resolve(ExampleDIDResolverSafeHandle @ptr,RustBuffer @did,OnDIDResolverResultSafeHandle @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnGetSecretResult_object_free(IntPtr @ptr,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void didcomm_f8e5_OnGetSecretResult_success(OnGetSecretResultSafeHandle @ptr,RustBuffer @result,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void didcomm_f8e5_OnGetSecretResult_error(OnGetSecretResultSafeHandle @ptr,RustBuffer @err,RustBuffer @msg,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnFindSecretsResult_object_free(IntPtr @ptr,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void didcomm_f8e5_OnFindSecretsResult_success(OnFindSecretsResultSafeHandle @ptr,RustBuffer @result,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void didcomm_f8e5_OnFindSecretsResult_error(OnFindSecretsResultSafeHandle @ptr,RustBuffer @err,RustBuffer @msg,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_ExampleSecretsResolver_object_free(IntPtr @ptr,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern ExampleSecretsResolverSafeHandle didcomm_f8e5_ExampleSecretsResolver_new(RustBuffer @knownSecrets,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_ExampleSecretsResolver_get_secret(ExampleSecretsResolverSafeHandle @ptr,RustBuffer @secretId,OnGetSecretResultSafeHandle @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer didcomm_f8e5_ExampleSecretsResolver_find_secrets(ExampleSecretsResolverSafeHandle @ptr,RustBuffer @secretIds,OnFindSecretsResultSafeHandle @cb,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_DIDResolver_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_SecretsResolver_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnPackSignedResult_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnPackEncryptedResult_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnPackPlaintextResult_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnUnpackResult_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnFromPriorPackResult_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnFromPriorUnpackResult_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_OnWrapInForwardResult_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer ffi_didcomm_f8e5_rustbuffer_alloc(int @size,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer ffi_didcomm_f8e5_rustbuffer_from_bytes(ForeignBytes @bytes,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern void ffi_didcomm_f8e5_rustbuffer_free(RustBuffer @buf,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("didcomm_uniffi.dll")]
    public static extern RustBuffer ffi_didcomm_f8e5_rustbuffer_reserve(RustBuffer @buf,int @additional,
    ref RustCallStatus _uniffi_out_err
    );
   
}

// Public interface members begin here.

#pragma warning disable 8625




class FfiConverterULong: FfiConverter<ulong, ulong> {
    public static FfiConverterULong INSTANCE = new FfiConverterULong();

    public override ulong Lift(ulong value) {
        return value;
    }

    public override ulong Read(BigEndianStream stream) {
        return stream.ReadULong();
    }

    public override ulong Lower(ulong value) {
        return value;
    }

    public override int AllocationSize(ulong value) {
        return 8;
    }

    public override void Write(ulong value, BigEndianStream stream) {
        stream.WriteULong(value);
    }
}



class FfiConverterBoolean: FfiConverter<bool, sbyte> {
    public static FfiConverterBoolean INSTANCE = new FfiConverterBoolean();

    public override bool Lift(sbyte value) {
        return value != 0;
    }

    public override bool Read(BigEndianStream stream) {
        return Lift(stream.ReadSByte());
    }

    public override sbyte Lower(bool value) {
        return value ? (sbyte)1 : (sbyte)0;
    }

    public override int AllocationSize(bool value) {
        return (sbyte)1;
    }

    public override void Write(bool value, BigEndianStream stream) {
        stream.WriteSByte(Lower(value));
    }
}



class FfiConverterString: FfiConverter<string, RustBuffer> {
    public static FfiConverterString INSTANCE = new FfiConverterString();

    // Note: we don't inherit from FfiConverterRustBuffer, because we use a
    // special encoding when lowering/lifting.  We can use `RustBuffer.len` to
    // store our length and avoid writing it out to the buffer.
    public override string Lift(RustBuffer value) {
        try {
            var bytes = value.AsStream().ReadBytes(value.len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        } finally {
            RustBuffer.Free(value);
        }
    }

    public override string Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var bytes = stream.ReadBytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public override RustBuffer Lower(string value) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var rbuf = RustBuffer.Alloc(bytes.Length);
        rbuf.AsWriteableStream().WriteBytes(bytes);
        return rbuf;
    }

    // TODO(CS)
    // We aren't sure exactly how many bytes our string will be once it's UTF-8
    // encoded.  Allocate 3 bytes per unicode codepoint which will always be
    // enough.
    public override int AllocationSize(string value) {
        const int sizeForLength = 4;
        var sizeForString = value.Length * 3;
        return sizeForLength + sizeForString;
    }

    public override void Write(string value, BigEndianStream stream) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        stream.WriteInt(bytes.Length);
        stream.WriteBytes(bytes);
    }
}




// `SafeHandle` implements the semantics outlined below, i.e. its thread safe, and the dispose
// method will only be called once, once all outstanding native calls have completed.
// https://github.com/mozilla/uniffi-rs/blob/0dc031132d9493ca812c3af6e7dd60ad2ea95bf0/uniffi_bindgen/src/bindings/kotlin/templates/ObjectRuntime.kt#L31
// https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.criticalhandle

public abstract class FFIObject<THandle>: IDisposable where THandle : FFISafeHandle {
    private THandle handle;

    public FFIObject(THandle handle) {
        this.handle = handle;
    }

    public THandle GetHandle() {
        return handle;
    }

    public void Dispose() {
        handle.Dispose();
    }
}

public abstract class FFISafeHandle: SafeHandle {
    public FFISafeHandle(): base(new IntPtr(0), true) {
    }

    public FFISafeHandle(IntPtr pointer): this() {
        this.SetHandle(pointer);
    }

    public override bool IsInvalid {
        get {
            return handle.ToInt64() == 0;
        }
    }

    // TODO(CS) this completely breaks any guarantees offered by SafeHandle.. Extracting
    // raw value from SafeHandle puts responsiblity on the consumer of this function to
    // ensure that SafeHandle outlives the stream, and anyone who might have read the raw
    // value from the stream and are holding onto it. Otherwise, the result might be a use
    // after free, or free while method calls are still in flight.
    //
    // This is also relevant for Kotlin.
    //
    public IntPtr DangerousGetRawFfiValue() {
        return handle;
    }
}

static class FFIObjectUtil {
    public static void DisposeAll(params Object?[] list) {
        foreach (var obj in list) {
            Dispose(obj);
        }
    }

    // Dispose is implemented by recursive type inspection at runtime. This is because
    // generating correct Dispose calls for recursive complex types, e.g. List<List<int>>
    // is quite cumbersome.
    private static void Dispose(dynamic? obj) {
        if (obj == null) {
            return;
        }

        if (obj is IDisposable disposable) {
            disposable.Dispose();
            return;
        }

        var type = obj.GetType();
        if (type != null) {
            if (type.IsGenericType) {
                if (type.GetGenericTypeDefinition().IsAssignableFrom(typeof(List<>))) {
                    foreach (var value in obj) {
                        Dispose(value);
                    }
                } else if (type.GetGenericTypeDefinition().IsAssignableFrom(typeof(Dictionary<,>))) {
                    foreach (var value in obj.Values) {
                        Dispose(value);
                    }
                }
            }
        }
    }
}
public interface IDidComm {
    
    ErrorCode PackPlaintext(Message @msg, OnPackPlaintextResult @cb);
    
    ErrorCode PackSigned(Message @msg, String @signBy, OnPackSignedResult @cb);
    
    ErrorCode PackEncrypted(Message @msg, String @to, String? @from, String? @signBy, PackEncryptedOptions @options, OnPackEncryptedResult @cb);
    
    ErrorCode Unpack(String @msg, UnpackOptions @options, OnUnpackResult @cb);
    
    ErrorCode PackFromPrior(FromPrior @msg, String? @issuerKid, OnFromPriorPackResult @cb);
    
    ErrorCode UnpackFromPrior(String @fromPriorJwt, OnFromPriorUnpackResult @cb);
    
    ErrorCode WrapInForward(String @msg, Dictionary<String, JsonValue> @headers, String @to, List<String> @routingKeys, AnonCryptAlg @encAlgAnon, OnWrapInForwardResult @cb);
    
}

public class DidCommSafeHandle: FFISafeHandle {
    public DidCommSafeHandle(): base() {
    }
    public DidCommSafeHandle(IntPtr pointer): base(pointer) {
    }
    override protected bool ReleaseHandle() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_DIDComm_object_free(this.handle, ref status);
        });
        return true;
    }
}
public class DidComm: FFIObject<DidCommSafeHandle>, IDidComm {
    public DidComm(DidCommSafeHandle pointer): base(pointer) {}
    public DidComm(DidResolver @didResolver, SecretsResolver @secretResolver) :
        this(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_DIDComm_new(FfiConverterTypeDidResolver.INSTANCE.Lower(@didResolver), FfiConverterTypeSecretsResolver.INSTANCE.Lower(@secretResolver), ref _status)
)) {}

    
    public ErrorCode PackPlaintext(Message @msg, OnPackPlaintextResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_DIDComm_pack_plaintext(this.GetHandle(), FfiConverterTypeMessage.INSTANCE.Lower(@msg), FfiConverterTypeOnPackPlaintextResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    
    public ErrorCode PackSigned(Message @msg, String @signBy, OnPackSignedResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_DIDComm_pack_signed(this.GetHandle(), FfiConverterTypeMessage.INSTANCE.Lower(@msg), FfiConverterString.INSTANCE.Lower(@signBy), FfiConverterTypeOnPackSignedResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    
    public ErrorCode PackEncrypted(Message @msg, String @to, String? @from, String? @signBy, PackEncryptedOptions @options, OnPackEncryptedResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_DIDComm_pack_encrypted(this.GetHandle(), FfiConverterTypeMessage.INSTANCE.Lower(@msg), FfiConverterString.INSTANCE.Lower(@to), FfiConverterOptionalString.INSTANCE.Lower(@from), FfiConverterOptionalString.INSTANCE.Lower(@signBy), FfiConverterTypePackEncryptedOptions.INSTANCE.Lower(@options), FfiConverterTypeOnPackEncryptedResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    
    public ErrorCode Unpack(String @msg, UnpackOptions @options, OnUnpackResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_DIDComm_unpack(this.GetHandle(), FfiConverterString.INSTANCE.Lower(@msg), FfiConverterTypeUnpackOptions.INSTANCE.Lower(@options), FfiConverterTypeOnUnpackResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    
    public ErrorCode PackFromPrior(FromPrior @msg, String? @issuerKid, OnFromPriorPackResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_DIDComm_pack_from_prior(this.GetHandle(), FfiConverterTypeFromPrior.INSTANCE.Lower(@msg), FfiConverterOptionalString.INSTANCE.Lower(@issuerKid), FfiConverterTypeOnFromPriorPackResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    
    public ErrorCode UnpackFromPrior(String @fromPriorJwt, OnFromPriorUnpackResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_DIDComm_unpack_from_prior(this.GetHandle(), FfiConverterString.INSTANCE.Lower(@fromPriorJwt), FfiConverterTypeOnFromPriorUnpackResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    
    public ErrorCode WrapInForward(String @msg, Dictionary<String, JsonValue> @headers, String @to, List<String> @routingKeys, AnonCryptAlg @encAlgAnon, OnWrapInForwardResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_DIDComm_wrap_in_forward(this.GetHandle(), FfiConverterString.INSTANCE.Lower(@msg), FfiConverterDictionaryStringJsonValue.INSTANCE.Lower(@headers), FfiConverterString.INSTANCE.Lower(@to), FfiConverterSequenceString.INSTANCE.Lower(@routingKeys), FfiConverterTypeAnonCryptAlg.INSTANCE.Lower(@encAlgAnon), FfiConverterTypeOnWrapInForwardResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    

    
}

class FfiConverterTypeDidComm: FfiConverter<DidComm, DidCommSafeHandle> {
    public static FfiConverterTypeDidComm INSTANCE = new FfiConverterTypeDidComm();

    public override DidCommSafeHandle Lower(DidComm value) {
        return value.GetHandle();
    }

    public override DidComm Lift(DidCommSafeHandle value) {
        return new DidComm(value);
    }

    public override DidComm Read(BigEndianStream stream) {
        return Lift(new DidCommSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(DidComm value) {
        return 8;
    }

    public override void Write(DidComm value, BigEndianStream stream) {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}



public interface IExampleDidResolver {
    
    ErrorCode Resolve(String @did, OnDidResolverResult @cb);
    
}

public class ExampleDidResolverSafeHandle: FFISafeHandle {
    public ExampleDidResolverSafeHandle(): base() {
    }
    public ExampleDidResolverSafeHandle(IntPtr pointer): base(pointer) {
    }
    override protected bool ReleaseHandle() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_ExampleDIDResolver_object_free(this.handle, ref status);
        });
        return true;
    }
}
public class ExampleDidResolver: FFIObject<ExampleDidResolverSafeHandle>, IExampleDidResolver {
    public ExampleDidResolver(ExampleDidResolverSafeHandle pointer): base(pointer) {}
    public ExampleDidResolver(List<DidDoc> @knownDids) :
        this(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_ExampleDIDResolver_new(FfiConverterSequenceTypeDidDoc.INSTANCE.Lower(@knownDids), ref _status)
)) {}

    
    public ErrorCode Resolve(String @did, OnDidResolverResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_ExampleDIDResolver_resolve(this.GetHandle(), FfiConverterString.INSTANCE.Lower(@did), FfiConverterTypeOnDidResolverResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    

    
}

class FfiConverterTypeExampleDidResolver: FfiConverter<ExampleDidResolver, ExampleDidResolverSafeHandle> {
    public static FfiConverterTypeExampleDidResolver INSTANCE = new FfiConverterTypeExampleDidResolver();

    public override ExampleDidResolverSafeHandle Lower(ExampleDidResolver value) {
        return value.GetHandle();
    }

    public override ExampleDidResolver Lift(ExampleDidResolverSafeHandle value) {
        return new ExampleDidResolver(value);
    }

    public override ExampleDidResolver Read(BigEndianStream stream) {
        return Lift(new ExampleDidResolverSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(ExampleDidResolver value) {
        return 8;
    }

    public override void Write(ExampleDidResolver value, BigEndianStream stream) {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}



public interface IExampleSecretsResolver {
    
    ErrorCode GetSecret(String @secretId, OnGetSecretResult @cb);
    
    ErrorCode FindSecrets(List<String> @secretIds, OnFindSecretsResult @cb);
    
}

public class ExampleSecretsResolverSafeHandle: FFISafeHandle {
    public ExampleSecretsResolverSafeHandle(): base() {
    }
    public ExampleSecretsResolverSafeHandle(IntPtr pointer): base(pointer) {
    }
    override protected bool ReleaseHandle() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_ExampleSecretsResolver_object_free(this.handle, ref status);
        });
        return true;
    }
}
public class ExampleSecretsResolver: FFIObject<ExampleSecretsResolverSafeHandle>, IExampleSecretsResolver {
    public ExampleSecretsResolver(ExampleSecretsResolverSafeHandle pointer): base(pointer) {}
    public ExampleSecretsResolver(List<Secret> @knownSecrets) :
        this(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_ExampleSecretsResolver_new(FfiConverterSequenceTypeSecret.INSTANCE.Lower(@knownSecrets), ref _status)
)) {}

    
    public ErrorCode GetSecret(String @secretId, OnGetSecretResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_ExampleSecretsResolver_get_secret(this.GetHandle(), FfiConverterString.INSTANCE.Lower(@secretId), FfiConverterTypeOnGetSecretResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    
    public ErrorCode FindSecrets(List<String> @secretIds, OnFindSecretsResult @cb) {
        return FfiConverterTypeErrorCode.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_ExampleSecretsResolver_find_secrets(this.GetHandle(), FfiConverterSequenceString.INSTANCE.Lower(@secretIds), FfiConverterTypeOnFindSecretsResult.INSTANCE.Lower(@cb), ref _status)
));
    }
    

    
}

class FfiConverterTypeExampleSecretsResolver: FfiConverter<ExampleSecretsResolver, ExampleSecretsResolverSafeHandle> {
    public static FfiConverterTypeExampleSecretsResolver INSTANCE = new FfiConverterTypeExampleSecretsResolver();

    public override ExampleSecretsResolverSafeHandle Lower(ExampleSecretsResolver value) {
        return value.GetHandle();
    }

    public override ExampleSecretsResolver Lift(ExampleSecretsResolverSafeHandle value) {
        return new ExampleSecretsResolver(value);
    }

    public override ExampleSecretsResolver Read(BigEndianStream stream) {
        return Lift(new ExampleSecretsResolverSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(ExampleSecretsResolver value) {
        return 8;
    }

    public override void Write(ExampleSecretsResolver value, BigEndianStream stream) {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}



public interface IOnDidResolverResult {
    
    /// <exception cref="ErrorKind"></exception>
    void Success(DidDoc? @result);
    
    /// <exception cref="ErrorKind"></exception>
    void Error(ErrorKind @err, String @msg);
    
}

public class OnDidResolverResultSafeHandle: FFISafeHandle {
    public OnDidResolverResultSafeHandle(): base() {
    }
    public OnDidResolverResultSafeHandle(IntPtr pointer): base(pointer) {
    }
    override protected bool ReleaseHandle() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnDIDResolverResult_object_free(this.handle, ref status);
        });
        return true;
    }
}
public class OnDidResolverResult: FFIObject<OnDidResolverResultSafeHandle>, IOnDidResolverResult {
    public OnDidResolverResult(OnDidResolverResultSafeHandle pointer): base(pointer) {}

    
    /// <exception cref="ErrorKind"></exception>
    public void Success(DidDoc? @result) {
    _UniffiHelpers.RustCallWithError(FfiConverterTypeErrorKind.INSTANCE, (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_OnDIDResolverResult_success(this.GetHandle(), FfiConverterOptionalTypeDidDoc.INSTANCE.Lower(@result), ref _status)
);
    }
    
    
    /// <exception cref="ErrorKind"></exception>
    public void Error(ErrorKind @err, String @msg) {
    _UniffiHelpers.RustCallWithError(FfiConverterTypeErrorKind.INSTANCE, (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_OnDIDResolverResult_error(this.GetHandle(), FfiConverterTypeErrorKind.INSTANCE.Lower(@err), FfiConverterString.INSTANCE.Lower(@msg), ref _status)
);
    }
    
    

    
}

class FfiConverterTypeOnDidResolverResult: FfiConverter<OnDidResolverResult, OnDidResolverResultSafeHandle> {
    public static FfiConverterTypeOnDidResolverResult INSTANCE = new FfiConverterTypeOnDidResolverResult();

    public override OnDidResolverResultSafeHandle Lower(OnDidResolverResult value) {
        return value.GetHandle();
    }

    public override OnDidResolverResult Lift(OnDidResolverResultSafeHandle value) {
        return new OnDidResolverResult(value);
    }

    public override OnDidResolverResult Read(BigEndianStream stream) {
        return Lift(new OnDidResolverResultSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(OnDidResolverResult value) {
        return 8;
    }

    public override void Write(OnDidResolverResult value, BigEndianStream stream) {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}



public interface IOnFindSecretsResult {
    
    /// <exception cref="ErrorKind"></exception>
    void Success(List<String> @result);
    
    /// <exception cref="ErrorKind"></exception>
    void Error(ErrorKind @err, String @msg);
    
}

public class OnFindSecretsResultSafeHandle: FFISafeHandle {
    public OnFindSecretsResultSafeHandle(): base() {
    }
    public OnFindSecretsResultSafeHandle(IntPtr pointer): base(pointer) {
    }
    override protected bool ReleaseHandle() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnFindSecretsResult_object_free(this.handle, ref status);
        });
        return true;
    }
}
public class OnFindSecretsResult: FFIObject<OnFindSecretsResultSafeHandle>, IOnFindSecretsResult {
    public OnFindSecretsResult(OnFindSecretsResultSafeHandle pointer): base(pointer) {}

    
    /// <exception cref="ErrorKind"></exception>
    public void Success(List<String> @result) {
    _UniffiHelpers.RustCallWithError(FfiConverterTypeErrorKind.INSTANCE, (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_OnFindSecretsResult_success(this.GetHandle(), FfiConverterSequenceString.INSTANCE.Lower(@result), ref _status)
);
    }
    
    
    /// <exception cref="ErrorKind"></exception>
    public void Error(ErrorKind @err, String @msg) {
    _UniffiHelpers.RustCallWithError(FfiConverterTypeErrorKind.INSTANCE, (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_OnFindSecretsResult_error(this.GetHandle(), FfiConverterTypeErrorKind.INSTANCE.Lower(@err), FfiConverterString.INSTANCE.Lower(@msg), ref _status)
);
    }
    
    

    
}

class FfiConverterTypeOnFindSecretsResult: FfiConverter<OnFindSecretsResult, OnFindSecretsResultSafeHandle> {
    public static FfiConverterTypeOnFindSecretsResult INSTANCE = new FfiConverterTypeOnFindSecretsResult();

    public override OnFindSecretsResultSafeHandle Lower(OnFindSecretsResult value) {
        return value.GetHandle();
    }

    public override OnFindSecretsResult Lift(OnFindSecretsResultSafeHandle value) {
        return new OnFindSecretsResult(value);
    }

    public override OnFindSecretsResult Read(BigEndianStream stream) {
        return Lift(new OnFindSecretsResultSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(OnFindSecretsResult value) {
        return 8;
    }

    public override void Write(OnFindSecretsResult value, BigEndianStream stream) {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}



public interface IOnGetSecretResult {
    
    /// <exception cref="ErrorKind"></exception>
    void Success(Secret? @result);
    
    /// <exception cref="ErrorKind"></exception>
    void Error(ErrorKind @err, String @msg);
    
}

public class OnGetSecretResultSafeHandle: FFISafeHandle {
    public OnGetSecretResultSafeHandle(): base() {
    }
    public OnGetSecretResultSafeHandle(IntPtr pointer): base(pointer) {
    }
    override protected bool ReleaseHandle() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnGetSecretResult_object_free(this.handle, ref status);
        });
        return true;
    }
}
public class OnGetSecretResult: FFIObject<OnGetSecretResultSafeHandle>, IOnGetSecretResult {
    public OnGetSecretResult(OnGetSecretResultSafeHandle pointer): base(pointer) {}

    
    /// <exception cref="ErrorKind"></exception>
    public void Success(Secret? @result) {
    _UniffiHelpers.RustCallWithError(FfiConverterTypeErrorKind.INSTANCE, (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_OnGetSecretResult_success(this.GetHandle(), FfiConverterOptionalTypeSecret.INSTANCE.Lower(@result), ref _status)
);
    }
    
    
    /// <exception cref="ErrorKind"></exception>
    public void Error(ErrorKind @err, String @msg) {
    _UniffiHelpers.RustCallWithError(FfiConverterTypeErrorKind.INSTANCE, (ref RustCallStatus _status) =>
    _UniFFILib.didcomm_f8e5_OnGetSecretResult_error(this.GetHandle(), FfiConverterTypeErrorKind.INSTANCE.Lower(@err), FfiConverterString.INSTANCE.Lower(@msg), ref _status)
);
    }
    
    

    
}

class FfiConverterTypeOnGetSecretResult: FfiConverter<OnGetSecretResult, OnGetSecretResultSafeHandle> {
    public static FfiConverterTypeOnGetSecretResult INSTANCE = new FfiConverterTypeOnGetSecretResult();

    public override OnGetSecretResultSafeHandle Lower(OnGetSecretResult value) {
        return value.GetHandle();
    }

    public override OnGetSecretResult Lift(OnGetSecretResultSafeHandle value) {
        return new OnGetSecretResult(value);
    }

    public override OnGetSecretResult Read(BigEndianStream stream) {
        return Lift(new OnGetSecretResultSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(OnGetSecretResult value) {
        return 8;
    }

    public override void Write(OnGetSecretResult value, BigEndianStream stream) {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}



public record Attachment (
    AttachmentData @data, 
    String? @id, 
    String? @description, 
    String? @filename, 
    String? @mediaType, 
    String? @format, 
    UInt64? @lastmodTime, 
    UInt64? @byteCount
) {
}

class FfiConverterTypeAttachment: FfiConverterRustBuffer<Attachment> {
    public static FfiConverterTypeAttachment INSTANCE = new FfiConverterTypeAttachment();

    public override Attachment Read(BigEndianStream stream) {
        return new Attachment(
            FfiConverterTypeAttachmentData.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalULong.INSTANCE.Read(stream),
            FfiConverterOptionalULong.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(Attachment value) {
        return
            FfiConverterTypeAttachmentData.INSTANCE.AllocationSize(value.@data) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@id) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@description) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@filename) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@mediaType) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@format) +
            FfiConverterOptionalULong.INSTANCE.AllocationSize(value.@lastmodTime) +
            FfiConverterOptionalULong.INSTANCE.AllocationSize(value.@byteCount);
    }

    public override void Write(Attachment value, BigEndianStream stream) {
            FfiConverterTypeAttachmentData.INSTANCE.Write(value.@data, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@id, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@description, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@filename, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@mediaType, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@format, stream);
            FfiConverterOptionalULong.INSTANCE.Write(value.@lastmodTime, stream);
            FfiConverterOptionalULong.INSTANCE.Write(value.@byteCount, stream);
    }
}



public record Base64AttachmentData (
    String @base64, 
    String? @jws
) {
}

class FfiConverterTypeBase64AttachmentData: FfiConverterRustBuffer<Base64AttachmentData> {
    public static FfiConverterTypeBase64AttachmentData INSTANCE = new FfiConverterTypeBase64AttachmentData();

    public override Base64AttachmentData Read(BigEndianStream stream) {
        return new Base64AttachmentData(
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(Base64AttachmentData value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@base64) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@jws);
    }

    public override void Write(Base64AttachmentData value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@base64, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@jws, stream);
    }
}



public record DidCommMessagingService (
    String @uri, 
    List<String>? @accept, 
    List<String> @routingKeys
) {
}

class FfiConverterTypeDidCommMessagingService: FfiConverterRustBuffer<DidCommMessagingService> {
    public static FfiConverterTypeDidCommMessagingService INSTANCE = new FfiConverterTypeDidCommMessagingService();

    public override DidCommMessagingService Read(BigEndianStream stream) {
        return new DidCommMessagingService(
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterOptionalSequenceString.INSTANCE.Read(stream),
            FfiConverterSequenceString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(DidCommMessagingService value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@uri) +
            FfiConverterOptionalSequenceString.INSTANCE.AllocationSize(value.@accept) +
            FfiConverterSequenceString.INSTANCE.AllocationSize(value.@routingKeys);
    }

    public override void Write(DidCommMessagingService value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@uri, stream);
            FfiConverterOptionalSequenceString.INSTANCE.Write(value.@accept, stream);
            FfiConverterSequenceString.INSTANCE.Write(value.@routingKeys, stream);
    }
}



public record DidDoc (
    String @id, 
    List<String> @keyAgreement, 
    List<String> @authentication, 
    List<VerificationMethod> @verificationMethod, 
    List<Service> @service
) {
}

class FfiConverterTypeDidDoc: FfiConverterRustBuffer<DidDoc> {
    public static FfiConverterTypeDidDoc INSTANCE = new FfiConverterTypeDidDoc();

    public override DidDoc Read(BigEndianStream stream) {
        return new DidDoc(
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterSequenceString.INSTANCE.Read(stream),
            FfiConverterSequenceString.INSTANCE.Read(stream),
            FfiConverterSequenceTypeVerificationMethod.INSTANCE.Read(stream),
            FfiConverterSequenceTypeService.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(DidDoc value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@id) +
            FfiConverterSequenceString.INSTANCE.AllocationSize(value.@keyAgreement) +
            FfiConverterSequenceString.INSTANCE.AllocationSize(value.@authentication) +
            FfiConverterSequenceTypeVerificationMethod.INSTANCE.AllocationSize(value.@verificationMethod) +
            FfiConverterSequenceTypeService.INSTANCE.AllocationSize(value.@service);
    }

    public override void Write(DidDoc value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@id, stream);
            FfiConverterSequenceString.INSTANCE.Write(value.@keyAgreement, stream);
            FfiConverterSequenceString.INSTANCE.Write(value.@authentication, stream);
            FfiConverterSequenceTypeVerificationMethod.INSTANCE.Write(value.@verificationMethod, stream);
            FfiConverterSequenceTypeService.INSTANCE.Write(value.@service, stream);
    }
}



public record FromPrior (
    String @iss, 
    String @sub, 
    String? @aud, 
    UInt64? @exp, 
    UInt64? @nbf, 
    UInt64? @iat, 
    String? @jti
) {
}

class FfiConverterTypeFromPrior: FfiConverterRustBuffer<FromPrior> {
    public static FfiConverterTypeFromPrior INSTANCE = new FfiConverterTypeFromPrior();

    public override FromPrior Read(BigEndianStream stream) {
        return new FromPrior(
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalULong.INSTANCE.Read(stream),
            FfiConverterOptionalULong.INSTANCE.Read(stream),
            FfiConverterOptionalULong.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(FromPrior value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@iss) +
            FfiConverterString.INSTANCE.AllocationSize(value.@sub) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@aud) +
            FfiConverterOptionalULong.INSTANCE.AllocationSize(value.@exp) +
            FfiConverterOptionalULong.INSTANCE.AllocationSize(value.@nbf) +
            FfiConverterOptionalULong.INSTANCE.AllocationSize(value.@iat) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@jti);
    }

    public override void Write(FromPrior value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@iss, stream);
            FfiConverterString.INSTANCE.Write(value.@sub, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@aud, stream);
            FfiConverterOptionalULong.INSTANCE.Write(value.@exp, stream);
            FfiConverterOptionalULong.INSTANCE.Write(value.@nbf, stream);
            FfiConverterOptionalULong.INSTANCE.Write(value.@iat, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@jti, stream);
    }
}



public record JsonAttachmentData (
    JsonValue @json, 
    String? @jws
) {
}

class FfiConverterTypeJsonAttachmentData: FfiConverterRustBuffer<JsonAttachmentData> {
    public static FfiConverterTypeJsonAttachmentData INSTANCE = new FfiConverterTypeJsonAttachmentData();

    public override JsonAttachmentData Read(BigEndianStream stream) {
        return new JsonAttachmentData(
            FfiConverterTypeJsonValue.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(JsonAttachmentData value) {
        return
            FfiConverterTypeJsonValue.INSTANCE.AllocationSize(value.@json) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@jws);
    }

    public override void Write(JsonAttachmentData value, BigEndianStream stream) {
            FfiConverterTypeJsonValue.INSTANCE.Write(value.@json, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@jws, stream);
    }
}



public record LinksAttachmentData (
    List<String> @links, 
    String @hash, 
    String? @jws
) {
}

class FfiConverterTypeLinksAttachmentData: FfiConverterRustBuffer<LinksAttachmentData> {
    public static FfiConverterTypeLinksAttachmentData INSTANCE = new FfiConverterTypeLinksAttachmentData();

    public override LinksAttachmentData Read(BigEndianStream stream) {
        return new LinksAttachmentData(
            FfiConverterSequenceString.INSTANCE.Read(stream),
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(LinksAttachmentData value) {
        return
            FfiConverterSequenceString.INSTANCE.AllocationSize(value.@links) +
            FfiConverterString.INSTANCE.AllocationSize(value.@hash) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@jws);
    }

    public override void Write(LinksAttachmentData value, BigEndianStream stream) {
            FfiConverterSequenceString.INSTANCE.Write(value.@links, stream);
            FfiConverterString.INSTANCE.Write(value.@hash, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@jws, stream);
    }
}



public record Message (
    String @id, 
    String @typ, 
    String @type, 
    JsonValue @body, 
    String? @from, 
    List<String>? @to, 
    String? @thid, 
    String? @pthid, 
    Dictionary<String, JsonValue> @extraHeaders, 
    UInt64? @createdTime, 
    UInt64? @expiresTime, 
    String? @fromPrior, 
    List<Attachment>? @attachments
) {
}

class FfiConverterTypeMessage: FfiConverterRustBuffer<Message> {
    public static FfiConverterTypeMessage INSTANCE = new FfiConverterTypeMessage();

    public override Message Read(BigEndianStream stream) {
        return new Message(
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterTypeJsonValue.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalSequenceString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterDictionaryStringJsonValue.INSTANCE.Read(stream),
            FfiConverterOptionalULong.INSTANCE.Read(stream),
            FfiConverterOptionalULong.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalSequenceTypeAttachment.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(Message value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@id) +
            FfiConverterString.INSTANCE.AllocationSize(value.@typ) +
            FfiConverterString.INSTANCE.AllocationSize(value.@type) +
            FfiConverterTypeJsonValue.INSTANCE.AllocationSize(value.@body) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@from) +
            FfiConverterOptionalSequenceString.INSTANCE.AllocationSize(value.@to) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@thid) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@pthid) +
            FfiConverterDictionaryStringJsonValue.INSTANCE.AllocationSize(value.@extraHeaders) +
            FfiConverterOptionalULong.INSTANCE.AllocationSize(value.@createdTime) +
            FfiConverterOptionalULong.INSTANCE.AllocationSize(value.@expiresTime) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@fromPrior) +
            FfiConverterOptionalSequenceTypeAttachment.INSTANCE.AllocationSize(value.@attachments);
    }

    public override void Write(Message value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@id, stream);
            FfiConverterString.INSTANCE.Write(value.@typ, stream);
            FfiConverterString.INSTANCE.Write(value.@type, stream);
            FfiConverterTypeJsonValue.INSTANCE.Write(value.@body, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@from, stream);
            FfiConverterOptionalSequenceString.INSTANCE.Write(value.@to, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@thid, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@pthid, stream);
            FfiConverterDictionaryStringJsonValue.INSTANCE.Write(value.@extraHeaders, stream);
            FfiConverterOptionalULong.INSTANCE.Write(value.@createdTime, stream);
            FfiConverterOptionalULong.INSTANCE.Write(value.@expiresTime, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@fromPrior, stream);
            FfiConverterOptionalSequenceTypeAttachment.INSTANCE.Write(value.@attachments, stream);
    }
}



public record MessagingServiceMetadata (
    String @id, 
    String @serviceEndpoint
) {
}

class FfiConverterTypeMessagingServiceMetadata: FfiConverterRustBuffer<MessagingServiceMetadata> {
    public static FfiConverterTypeMessagingServiceMetadata INSTANCE = new FfiConverterTypeMessagingServiceMetadata();

    public override MessagingServiceMetadata Read(BigEndianStream stream) {
        return new MessagingServiceMetadata(
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(MessagingServiceMetadata value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@id) +
            FfiConverterString.INSTANCE.AllocationSize(value.@serviceEndpoint);
    }

    public override void Write(MessagingServiceMetadata value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@id, stream);
            FfiConverterString.INSTANCE.Write(value.@serviceEndpoint, stream);
    }
}



public record PackEncryptedMetadata (
    MessagingServiceMetadata? @messagingService, 
    String? @fromKid, 
    String? @signByKid, 
    List<String> @toKids
) {
}

class FfiConverterTypePackEncryptedMetadata: FfiConverterRustBuffer<PackEncryptedMetadata> {
    public static FfiConverterTypePackEncryptedMetadata INSTANCE = new FfiConverterTypePackEncryptedMetadata();

    public override PackEncryptedMetadata Read(BigEndianStream stream) {
        return new PackEncryptedMetadata(
            FfiConverterOptionalTypeMessagingServiceMetadata.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterSequenceString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(PackEncryptedMetadata value) {
        return
            FfiConverterOptionalTypeMessagingServiceMetadata.INSTANCE.AllocationSize(value.@messagingService) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@fromKid) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@signByKid) +
            FfiConverterSequenceString.INSTANCE.AllocationSize(value.@toKids);
    }

    public override void Write(PackEncryptedMetadata value, BigEndianStream stream) {
            FfiConverterOptionalTypeMessagingServiceMetadata.INSTANCE.Write(value.@messagingService, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@fromKid, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@signByKid, stream);
            FfiConverterSequenceString.INSTANCE.Write(value.@toKids, stream);
    }
}



public record PackEncryptedOptions (
    Boolean @protectSender, 
    Boolean @forward, 
    Dictionary<String, JsonValue>? @forwardHeaders, 
    String? @messagingService, 
    AuthCryptAlg @encAlgAuth, 
    AnonCryptAlg @encAlgAnon
) {
}

class FfiConverterTypePackEncryptedOptions: FfiConverterRustBuffer<PackEncryptedOptions> {
    public static FfiConverterTypePackEncryptedOptions INSTANCE = new FfiConverterTypePackEncryptedOptions();

    public override PackEncryptedOptions Read(BigEndianStream stream) {
        return new PackEncryptedOptions(
            FfiConverterBoolean.INSTANCE.Read(stream),
            FfiConverterBoolean.INSTANCE.Read(stream),
            FfiConverterOptionalDictionaryStringJsonValue.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterTypeAuthCryptAlg.INSTANCE.Read(stream),
            FfiConverterTypeAnonCryptAlg.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(PackEncryptedOptions value) {
        return
            FfiConverterBoolean.INSTANCE.AllocationSize(value.@protectSender) +
            FfiConverterBoolean.INSTANCE.AllocationSize(value.@forward) +
            FfiConverterOptionalDictionaryStringJsonValue.INSTANCE.AllocationSize(value.@forwardHeaders) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@messagingService) +
            FfiConverterTypeAuthCryptAlg.INSTANCE.AllocationSize(value.@encAlgAuth) +
            FfiConverterTypeAnonCryptAlg.INSTANCE.AllocationSize(value.@encAlgAnon);
    }

    public override void Write(PackEncryptedOptions value, BigEndianStream stream) {
            FfiConverterBoolean.INSTANCE.Write(value.@protectSender, stream);
            FfiConverterBoolean.INSTANCE.Write(value.@forward, stream);
            FfiConverterOptionalDictionaryStringJsonValue.INSTANCE.Write(value.@forwardHeaders, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@messagingService, stream);
            FfiConverterTypeAuthCryptAlg.INSTANCE.Write(value.@encAlgAuth, stream);
            FfiConverterTypeAnonCryptAlg.INSTANCE.Write(value.@encAlgAnon, stream);
    }
}



public record PackSignedMetadata (
    String @signByKid
) {
}

class FfiConverterTypePackSignedMetadata: FfiConverterRustBuffer<PackSignedMetadata> {
    public static FfiConverterTypePackSignedMetadata INSTANCE = new FfiConverterTypePackSignedMetadata();

    public override PackSignedMetadata Read(BigEndianStream stream) {
        return new PackSignedMetadata(
            FfiConverterString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(PackSignedMetadata value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@signByKid);
    }

    public override void Write(PackSignedMetadata value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@signByKid, stream);
    }
}



public record Secret (
    String @id, 
    SecretType @type, 
    SecretMaterial @secretMaterial
) {
}

class FfiConverterTypeSecret: FfiConverterRustBuffer<Secret> {
    public static FfiConverterTypeSecret INSTANCE = new FfiConverterTypeSecret();

    public override Secret Read(BigEndianStream stream) {
        return new Secret(
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterTypeSecretType.INSTANCE.Read(stream),
            FfiConverterTypeSecretMaterial.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(Secret value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@id) +
            FfiConverterTypeSecretType.INSTANCE.AllocationSize(value.@type) +
            FfiConverterTypeSecretMaterial.INSTANCE.AllocationSize(value.@secretMaterial);
    }

    public override void Write(Secret value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@id, stream);
            FfiConverterTypeSecretType.INSTANCE.Write(value.@type, stream);
            FfiConverterTypeSecretMaterial.INSTANCE.Write(value.@secretMaterial, stream);
    }
}



public record Service (
    String @id, 
    ServiceKind @serviceEndpoint
) {
}

class FfiConverterTypeService: FfiConverterRustBuffer<Service> {
    public static FfiConverterTypeService INSTANCE = new FfiConverterTypeService();

    public override Service Read(BigEndianStream stream) {
        return new Service(
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterTypeServiceKind.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(Service value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@id) +
            FfiConverterTypeServiceKind.INSTANCE.AllocationSize(value.@serviceEndpoint);
    }

    public override void Write(Service value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@id, stream);
            FfiConverterTypeServiceKind.INSTANCE.Write(value.@serviceEndpoint, stream);
    }
}



public record UnpackMetadata (
    Boolean @encrypted, 
    Boolean @authenticated, 
    Boolean @nonRepudiation, 
    Boolean @anonymousSender, 
    Boolean @reWrappedInForward, 
    String? @encryptedFromKid, 
    List<String>? @encryptedToKids, 
    String? @signFrom, 
    String? @fromPriorIssuerKid, 
    AuthCryptAlg? @encAlgAuth, 
    AnonCryptAlg? @encAlgAnon, 
    SignAlg? @signAlg, 
    String? @signedMessage, 
    FromPrior? @fromPrior
) {
}

class FfiConverterTypeUnpackMetadata: FfiConverterRustBuffer<UnpackMetadata> {
    public static FfiConverterTypeUnpackMetadata INSTANCE = new FfiConverterTypeUnpackMetadata();

    public override UnpackMetadata Read(BigEndianStream stream) {
        return new UnpackMetadata(
            FfiConverterBoolean.INSTANCE.Read(stream),
            FfiConverterBoolean.INSTANCE.Read(stream),
            FfiConverterBoolean.INSTANCE.Read(stream),
            FfiConverterBoolean.INSTANCE.Read(stream),
            FfiConverterBoolean.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalSequenceString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalTypeAuthCryptAlg.INSTANCE.Read(stream),
            FfiConverterOptionalTypeAnonCryptAlg.INSTANCE.Read(stream),
            FfiConverterOptionalTypeSignAlg.INSTANCE.Read(stream),
            FfiConverterOptionalString.INSTANCE.Read(stream),
            FfiConverterOptionalTypeFromPrior.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(UnpackMetadata value) {
        return
            FfiConverterBoolean.INSTANCE.AllocationSize(value.@encrypted) +
            FfiConverterBoolean.INSTANCE.AllocationSize(value.@authenticated) +
            FfiConverterBoolean.INSTANCE.AllocationSize(value.@nonRepudiation) +
            FfiConverterBoolean.INSTANCE.AllocationSize(value.@anonymousSender) +
            FfiConverterBoolean.INSTANCE.AllocationSize(value.@reWrappedInForward) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@encryptedFromKid) +
            FfiConverterOptionalSequenceString.INSTANCE.AllocationSize(value.@encryptedToKids) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@signFrom) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@fromPriorIssuerKid) +
            FfiConverterOptionalTypeAuthCryptAlg.INSTANCE.AllocationSize(value.@encAlgAuth) +
            FfiConverterOptionalTypeAnonCryptAlg.INSTANCE.AllocationSize(value.@encAlgAnon) +
            FfiConverterOptionalTypeSignAlg.INSTANCE.AllocationSize(value.@signAlg) +
            FfiConverterOptionalString.INSTANCE.AllocationSize(value.@signedMessage) +
            FfiConverterOptionalTypeFromPrior.INSTANCE.AllocationSize(value.@fromPrior);
    }

    public override void Write(UnpackMetadata value, BigEndianStream stream) {
            FfiConverterBoolean.INSTANCE.Write(value.@encrypted, stream);
            FfiConverterBoolean.INSTANCE.Write(value.@authenticated, stream);
            FfiConverterBoolean.INSTANCE.Write(value.@nonRepudiation, stream);
            FfiConverterBoolean.INSTANCE.Write(value.@anonymousSender, stream);
            FfiConverterBoolean.INSTANCE.Write(value.@reWrappedInForward, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@encryptedFromKid, stream);
            FfiConverterOptionalSequenceString.INSTANCE.Write(value.@encryptedToKids, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@signFrom, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@fromPriorIssuerKid, stream);
            FfiConverterOptionalTypeAuthCryptAlg.INSTANCE.Write(value.@encAlgAuth, stream);
            FfiConverterOptionalTypeAnonCryptAlg.INSTANCE.Write(value.@encAlgAnon, stream);
            FfiConverterOptionalTypeSignAlg.INSTANCE.Write(value.@signAlg, stream);
            FfiConverterOptionalString.INSTANCE.Write(value.@signedMessage, stream);
            FfiConverterOptionalTypeFromPrior.INSTANCE.Write(value.@fromPrior, stream);
    }
}



public record UnpackOptions (
    Boolean @expectDecryptByAllKeys, 
    Boolean @unwrapReWrappingForward
) {
}

class FfiConverterTypeUnpackOptions: FfiConverterRustBuffer<UnpackOptions> {
    public static FfiConverterTypeUnpackOptions INSTANCE = new FfiConverterTypeUnpackOptions();

    public override UnpackOptions Read(BigEndianStream stream) {
        return new UnpackOptions(
            FfiConverterBoolean.INSTANCE.Read(stream),
            FfiConverterBoolean.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(UnpackOptions value) {
        return
            FfiConverterBoolean.INSTANCE.AllocationSize(value.@expectDecryptByAllKeys) +
            FfiConverterBoolean.INSTANCE.AllocationSize(value.@unwrapReWrappingForward);
    }

    public override void Write(UnpackOptions value, BigEndianStream stream) {
            FfiConverterBoolean.INSTANCE.Write(value.@expectDecryptByAllKeys, stream);
            FfiConverterBoolean.INSTANCE.Write(value.@unwrapReWrappingForward, stream);
    }
}



public record VerificationMethod (
    String @id, 
    VerificationMethodType @type, 
    String @controller, 
    VerificationMaterial @verificationMaterial
) {
}

class FfiConverterTypeVerificationMethod: FfiConverterRustBuffer<VerificationMethod> {
    public static FfiConverterTypeVerificationMethod INSTANCE = new FfiConverterTypeVerificationMethod();

    public override VerificationMethod Read(BigEndianStream stream) {
        return new VerificationMethod(
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterTypeVerificationMethodType.INSTANCE.Read(stream),
            FfiConverterString.INSTANCE.Read(stream),
            FfiConverterTypeVerificationMaterial.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(VerificationMethod value) {
        return
            FfiConverterString.INSTANCE.AllocationSize(value.@id) +
            FfiConverterTypeVerificationMethodType.INSTANCE.AllocationSize(value.@type) +
            FfiConverterString.INSTANCE.AllocationSize(value.@controller) +
            FfiConverterTypeVerificationMaterial.INSTANCE.AllocationSize(value.@verificationMaterial);
    }

    public override void Write(VerificationMethod value, BigEndianStream stream) {
            FfiConverterString.INSTANCE.Write(value.@id, stream);
            FfiConverterTypeVerificationMethodType.INSTANCE.Write(value.@type, stream);
            FfiConverterString.INSTANCE.Write(value.@controller, stream);
            FfiConverterTypeVerificationMaterial.INSTANCE.Write(value.@verificationMaterial, stream);
    }
}





public enum AnonCryptAlg: int {
    
    A256CBC_HS512_ECDH_ES_A256KW,
    XC20P_ECDH_ES_A256KW,
    A256GCM_ECDH_ES_A256KW
}

class FfiConverterTypeAnonCryptAlg: FfiConverterRustBuffer<AnonCryptAlg> {
    public static FfiConverterTypeAnonCryptAlg INSTANCE = new FfiConverterTypeAnonCryptAlg();

    public override AnonCryptAlg Read(BigEndianStream stream) {
        var value = stream.ReadInt() - 1;
        if (Enum.IsDefined(typeof(AnonCryptAlg), value)) {
            return (AnonCryptAlg)value;
        } else {
            throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeAnonCryptAlg.Read()", value));
        }
    }

    public override int AllocationSize(AnonCryptAlg value) {
        return 4;
    }

    public override void Write(AnonCryptAlg value, BigEndianStream stream) {
        stream.WriteInt((int)value + 1);
    }
}







public record AttachmentData {
    
    public record Base64 (
        Base64AttachmentData @value
    ) : AttachmentData {}
    
    public record Json (
        JsonAttachmentData @value
    ) : AttachmentData {}
    
    public record Links (
        LinksAttachmentData @value
    ) : AttachmentData {}
    

    
}

class FfiConverterTypeAttachmentData : FfiConverterRustBuffer<AttachmentData>{
    public static FfiConverterRustBuffer<AttachmentData> INSTANCE = new FfiConverterTypeAttachmentData();

    public override AttachmentData Read(BigEndianStream stream) {
        var value = stream.ReadInt();
        switch (value) {
            case 1:
                return new AttachmentData.Base64(
                    FfiConverterTypeBase64AttachmentData.INSTANCE.Read(stream)
                );
            case 2:
                return new AttachmentData.Json(
                    FfiConverterTypeJsonAttachmentData.INSTANCE.Read(stream)
                );
            case 3:
                return new AttachmentData.Links(
                    FfiConverterTypeLinksAttachmentData.INSTANCE.Read(stream)
                );
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeAttachmentData.Read()", value));
        }
    }

    public override int AllocationSize(AttachmentData value) {
        switch (value) {
            case AttachmentData.Base64 variant_value:
                return 4
                    + FfiConverterTypeBase64AttachmentData.INSTANCE.AllocationSize(variant_value.@value);
            case AttachmentData.Json variant_value:
                return 4
                    + FfiConverterTypeJsonAttachmentData.INSTANCE.AllocationSize(variant_value.@value);
            case AttachmentData.Links variant_value:
                return 4
                    + FfiConverterTypeLinksAttachmentData.INSTANCE.AllocationSize(variant_value.@value);
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeAttachmentData.AllocationSize()", value));
        }
    }

    public override void Write(AttachmentData value, BigEndianStream stream) {
        switch (value) {
            case AttachmentData.Base64 variant_value:
                stream.WriteInt(1);
                FfiConverterTypeBase64AttachmentData.INSTANCE.Write(variant_value.@value, stream);
                break;
            case AttachmentData.Json variant_value:
                stream.WriteInt(2);
                FfiConverterTypeJsonAttachmentData.INSTANCE.Write(variant_value.@value, stream);
                break;
            case AttachmentData.Links variant_value:
                stream.WriteInt(3);
                FfiConverterTypeLinksAttachmentData.INSTANCE.Write(variant_value.@value, stream);
                break;
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeAttachmentData.Write()", value));
        }
    }
}







public enum AuthCryptAlg: int {
    
    A256CBC_HS512_ECDH1PU_A256KW
}

class FfiConverterTypeAuthCryptAlg: FfiConverterRustBuffer<AuthCryptAlg> {
    public static FfiConverterTypeAuthCryptAlg INSTANCE = new FfiConverterTypeAuthCryptAlg();

    public override AuthCryptAlg Read(BigEndianStream stream) {
        var value = stream.ReadInt() - 1;
        if (Enum.IsDefined(typeof(AuthCryptAlg), value)) {
            return (AuthCryptAlg)value;
        } else {
            throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeAuthCryptAlg.Read()", value));
        }
    }

    public override int AllocationSize(AuthCryptAlg value) {
        return 4;
    }

    public override void Write(AuthCryptAlg value, BigEndianStream stream) {
        stream.WriteInt((int)value + 1);
    }
}







public enum ErrorCode: int {
    
    SUCCESS,
    ERROR
}

class FfiConverterTypeErrorCode: FfiConverterRustBuffer<ErrorCode> {
    public static FfiConverterTypeErrorCode INSTANCE = new FfiConverterTypeErrorCode();

    public override ErrorCode Read(BigEndianStream stream) {
        var value = stream.ReadInt() - 1;
        if (Enum.IsDefined(typeof(ErrorCode), value)) {
            return (ErrorCode)value;
        } else {
            throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeErrorCode.Read()", value));
        }
    }

    public override int AllocationSize(ErrorCode value) {
        return 4;
    }

    public override void Write(ErrorCode value, BigEndianStream stream) {
        stream.WriteInt((int)value + 1);
    }
}







public record SecretMaterial {
    
    public record Jwk (
        JsonValue @privateKeyJwk
    ) : SecretMaterial {}
    
    public record Multibase (
        String @privateKeyMultibase
    ) : SecretMaterial {}
    
    public record Base58 (
        String @privateKeyBase58
    ) : SecretMaterial {}
    

    
}

class FfiConverterTypeSecretMaterial : FfiConverterRustBuffer<SecretMaterial>{
    public static FfiConverterRustBuffer<SecretMaterial> INSTANCE = new FfiConverterTypeSecretMaterial();

    public override SecretMaterial Read(BigEndianStream stream) {
        var value = stream.ReadInt();
        switch (value) {
            case 1:
                return new SecretMaterial.Jwk(
                    FfiConverterTypeJsonValue.INSTANCE.Read(stream)
                );
            case 2:
                return new SecretMaterial.Multibase(
                    FfiConverterString.INSTANCE.Read(stream)
                );
            case 3:
                return new SecretMaterial.Base58(
                    FfiConverterString.INSTANCE.Read(stream)
                );
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeSecretMaterial.Read()", value));
        }
    }

    public override int AllocationSize(SecretMaterial value) {
        switch (value) {
            case SecretMaterial.Jwk variant_value:
                return 4
                    + FfiConverterTypeJsonValue.INSTANCE.AllocationSize(variant_value.@privateKeyJwk);
            case SecretMaterial.Multibase variant_value:
                return 4
                    + FfiConverterString.INSTANCE.AllocationSize(variant_value.@privateKeyMultibase);
            case SecretMaterial.Base58 variant_value:
                return 4
                    + FfiConverterString.INSTANCE.AllocationSize(variant_value.@privateKeyBase58);
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeSecretMaterial.AllocationSize()", value));
        }
    }

    public override void Write(SecretMaterial value, BigEndianStream stream) {
        switch (value) {
            case SecretMaterial.Jwk variant_value:
                stream.WriteInt(1);
                FfiConverterTypeJsonValue.INSTANCE.Write(variant_value.@privateKeyJwk, stream);
                break;
            case SecretMaterial.Multibase variant_value:
                stream.WriteInt(2);
                FfiConverterString.INSTANCE.Write(variant_value.@privateKeyMultibase, stream);
                break;
            case SecretMaterial.Base58 variant_value:
                stream.WriteInt(3);
                FfiConverterString.INSTANCE.Write(variant_value.@privateKeyBase58, stream);
                break;
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeSecretMaterial.Write()", value));
        }
    }
}







public enum SecretType: int {
    
    JSON_WEB_KEY2020,
    X25519_KEY_AGREEMENT_KEY2019,
    ED25519_VERIFICATION_KEY2018,
    ECDSA_SECP256K1_VERIFICATION_KEY2019,
    X25519_KEY_AGREEMENT_KEY2020,
    ED25519_VERIFICATION_KEY2020,
    OTHER
}

class FfiConverterTypeSecretType: FfiConverterRustBuffer<SecretType> {
    public static FfiConverterTypeSecretType INSTANCE = new FfiConverterTypeSecretType();

    public override SecretType Read(BigEndianStream stream) {
        var value = stream.ReadInt() - 1;
        if (Enum.IsDefined(typeof(SecretType), value)) {
            return (SecretType)value;
        } else {
            throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeSecretType.Read()", value));
        }
    }

    public override int AllocationSize(SecretType value) {
        return 4;
    }

    public override void Write(SecretType value, BigEndianStream stream) {
        stream.WriteInt((int)value + 1);
    }
}







public record ServiceKind {
    
    public record DidCommMessaging (
        DidCommMessagingService @value
    ) : ServiceKind {}
    
    public record Other (
        JsonValue @value
    ) : ServiceKind {}
    

    
}

class FfiConverterTypeServiceKind : FfiConverterRustBuffer<ServiceKind>{
    public static FfiConverterRustBuffer<ServiceKind> INSTANCE = new FfiConverterTypeServiceKind();

    public override ServiceKind Read(BigEndianStream stream) {
        var value = stream.ReadInt();
        switch (value) {
            case 1:
                return new ServiceKind.DidCommMessaging(
                    FfiConverterTypeDidCommMessagingService.INSTANCE.Read(stream)
                );
            case 2:
                return new ServiceKind.Other(
                    FfiConverterTypeJsonValue.INSTANCE.Read(stream)
                );
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeServiceKind.Read()", value));
        }
    }

    public override int AllocationSize(ServiceKind value) {
        switch (value) {
            case ServiceKind.DidCommMessaging variant_value:
                return 4
                    + FfiConverterTypeDidCommMessagingService.INSTANCE.AllocationSize(variant_value.@value);
            case ServiceKind.Other variant_value:
                return 4
                    + FfiConverterTypeJsonValue.INSTANCE.AllocationSize(variant_value.@value);
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeServiceKind.AllocationSize()", value));
        }
    }

    public override void Write(ServiceKind value, BigEndianStream stream) {
        switch (value) {
            case ServiceKind.DidCommMessaging variant_value:
                stream.WriteInt(1);
                FfiConverterTypeDidCommMessagingService.INSTANCE.Write(variant_value.@value, stream);
                break;
            case ServiceKind.Other variant_value:
                stream.WriteInt(2);
                FfiConverterTypeJsonValue.INSTANCE.Write(variant_value.@value, stream);
                break;
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeServiceKind.Write()", value));
        }
    }
}







public enum SignAlg: int {
    
    ED_DSA,
    ES256,
    ES256K
}

class FfiConverterTypeSignAlg: FfiConverterRustBuffer<SignAlg> {
    public static FfiConverterTypeSignAlg INSTANCE = new FfiConverterTypeSignAlg();

    public override SignAlg Read(BigEndianStream stream) {
        var value = stream.ReadInt() - 1;
        if (Enum.IsDefined(typeof(SignAlg), value)) {
            return (SignAlg)value;
        } else {
            throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeSignAlg.Read()", value));
        }
    }

    public override int AllocationSize(SignAlg value) {
        return 4;
    }

    public override void Write(SignAlg value, BigEndianStream stream) {
        stream.WriteInt((int)value + 1);
    }
}







public record VerificationMaterial {
    
    public record Jwk (
        JsonValue @publicKeyJwk
    ) : VerificationMaterial {}
    
    public record Multibase (
        String @publicKeyMultibase
    ) : VerificationMaterial {}
    
    public record Base58 (
        String @publicKeyBase58
    ) : VerificationMaterial {}
    

    
}

class FfiConverterTypeVerificationMaterial : FfiConverterRustBuffer<VerificationMaterial>{
    public static FfiConverterRustBuffer<VerificationMaterial> INSTANCE = new FfiConverterTypeVerificationMaterial();

    public override VerificationMaterial Read(BigEndianStream stream) {
        var value = stream.ReadInt();
        switch (value) {
            case 1:
                return new VerificationMaterial.Jwk(
                    FfiConverterTypeJsonValue.INSTANCE.Read(stream)
                );
            case 2:
                return new VerificationMaterial.Multibase(
                    FfiConverterString.INSTANCE.Read(stream)
                );
            case 3:
                return new VerificationMaterial.Base58(
                    FfiConverterString.INSTANCE.Read(stream)
                );
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeVerificationMaterial.Read()", value));
        }
    }

    public override int AllocationSize(VerificationMaterial value) {
        switch (value) {
            case VerificationMaterial.Jwk variant_value:
                return 4
                    + FfiConverterTypeJsonValue.INSTANCE.AllocationSize(variant_value.@publicKeyJwk);
            case VerificationMaterial.Multibase variant_value:
                return 4
                    + FfiConverterString.INSTANCE.AllocationSize(variant_value.@publicKeyMultibase);
            case VerificationMaterial.Base58 variant_value:
                return 4
                    + FfiConverterString.INSTANCE.AllocationSize(variant_value.@publicKeyBase58);
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeVerificationMaterial.AllocationSize()", value));
        }
    }

    public override void Write(VerificationMaterial value, BigEndianStream stream) {
        switch (value) {
            case VerificationMaterial.Jwk variant_value:
                stream.WriteInt(1);
                FfiConverterTypeJsonValue.INSTANCE.Write(variant_value.@publicKeyJwk, stream);
                break;
            case VerificationMaterial.Multibase variant_value:
                stream.WriteInt(2);
                FfiConverterString.INSTANCE.Write(variant_value.@publicKeyMultibase, stream);
                break;
            case VerificationMaterial.Base58 variant_value:
                stream.WriteInt(3);
                FfiConverterString.INSTANCE.Write(variant_value.@publicKeyBase58, stream);
                break;
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeVerificationMaterial.Write()", value));
        }
    }
}







public enum VerificationMethodType: int {
    
    JSON_WEB_KEY2020,
    X25519_KEY_AGREEMENT_KEY2019,
    ED25519_VERIFICATION_KEY2018,
    ECDSA_SECP256K1_VERIFICATION_KEY2019,
    X25519_KEY_AGREEMENT_KEY2020,
    ED25519_VERIFICATION_KEY2020,
    OTHER
}

class FfiConverterTypeVerificationMethodType: FfiConverterRustBuffer<VerificationMethodType> {
    public static FfiConverterTypeVerificationMethodType INSTANCE = new FfiConverterTypeVerificationMethodType();

    public override VerificationMethodType Read(BigEndianStream stream) {
        var value = stream.ReadInt() - 1;
        if (Enum.IsDefined(typeof(VerificationMethodType), value)) {
            return (VerificationMethodType)value;
        } else {
            throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeVerificationMethodType.Read()", value));
        }
    }

    public override int AllocationSize(VerificationMethodType value) {
        return 4;
    }

    public override void Write(VerificationMethodType value, BigEndianStream stream) {
        stream.WriteInt((int)value + 1);
    }
}







public class ErrorKind: UniffiException {
    ErrorKind(string message): base(message) {}

    // Each variant is a nested class
    // Flat enums carries a string error message, so no special implementation is necessary.
    
    public class DidNotResolved: ErrorKind {
        public DidNotResolved(string message): base(message) {}
    }
    
    public class DidUrlNotFound: ErrorKind {
        public DidUrlNotFound(string message): base(message) {}
    }
    
    public class SecretNotFound: ErrorKind {
        public SecretNotFound(string message): base(message) {}
    }
    
    public class Malformed: ErrorKind {
        public Malformed(string message): base(message) {}
    }
    
    public class IoException: ErrorKind {
        public IoException(string message): base(message) {}
    }
    
    public class InvalidState: ErrorKind {
        public InvalidState(string message): base(message) {}
    }
    
    public class NoCompatibleCrypto: ErrorKind {
        public NoCompatibleCrypto(string message): base(message) {}
    }
    
    public class Unsupported: ErrorKind {
        public Unsupported(string message): base(message) {}
    }
    
    public class IllegalArgument: ErrorKind {
        public IllegalArgument(string message): base(message) {}
    }
    
}

class FfiConverterTypeErrorKind : FfiConverterRustBuffer<ErrorKind>, CallStatusErrorHandler<ErrorKind> {
    public static FfiConverterTypeErrorKind INSTANCE = new FfiConverterTypeErrorKind();

    public override ErrorKind Read(BigEndianStream stream) {
        var value = stream.ReadInt();
        switch (value) {
            case 1: return new ErrorKind.DidNotResolved(FfiConverterString.INSTANCE.Read(stream));
            case 2: return new ErrorKind.DidUrlNotFound(FfiConverterString.INSTANCE.Read(stream));
            case 3: return new ErrorKind.SecretNotFound(FfiConverterString.INSTANCE.Read(stream));
            case 4: return new ErrorKind.Malformed(FfiConverterString.INSTANCE.Read(stream));
            case 5: return new ErrorKind.IoException(FfiConverterString.INSTANCE.Read(stream));
            case 6: return new ErrorKind.InvalidState(FfiConverterString.INSTANCE.Read(stream));
            case 7: return new ErrorKind.NoCompatibleCrypto(FfiConverterString.INSTANCE.Read(stream));
            case 8: return new ErrorKind.Unsupported(FfiConverterString.INSTANCE.Read(stream));
            case 9: return new ErrorKind.IllegalArgument(FfiConverterString.INSTANCE.Read(stream));
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeErrorKind.Read()", value));
        }
    }

    public override int AllocationSize(ErrorKind value) {
        return 4 + FfiConverterString.INSTANCE.AllocationSize(value.Message);
    }

    public override void Write(ErrorKind value, BigEndianStream stream) {
        switch (value) {
            case ErrorKind.DidNotResolved:
                stream.WriteInt(1);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case ErrorKind.DidUrlNotFound:
                stream.WriteInt(2);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case ErrorKind.SecretNotFound:
                stream.WriteInt(3);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case ErrorKind.Malformed:
                stream.WriteInt(4);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case ErrorKind.IoException:
                stream.WriteInt(5);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case ErrorKind.InvalidState:
                stream.WriteInt(6);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case ErrorKind.NoCompatibleCrypto:
                stream.WriteInt(7);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case ErrorKind.Unsupported:
                stream.WriteInt(8);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case ErrorKind.IllegalArgument:
                stream.WriteInt(9);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            default:
                throw new InternalException(String.Format("invalid enum value '{}' in FfiConverterTypeErrorKind.Write()", value));
        }
    }
}






static class UniffiCallbackConstants {
    public static int SUCCESS = 0;
    public static int ERROR = 1;
    public static int UNEXPECTED_ERROR = 2;
}

class ConcurrentHandleMap<T> where T: notnull {
    Dictionary<ulong, T> leftMap = new Dictionary<ulong, T>();
    Dictionary<T, ulong> rightMap = new Dictionary<T, ulong>();

    Object lock_ = new Object();
    ulong currentHandle = 0;

    public ulong Insert(T obj) {
        lock (lock_) {
            ulong existingHandle = 0;
            if (rightMap.TryGetValue(obj, out existingHandle)) {
                return existingHandle;
            }
            currentHandle += 1;
            leftMap[currentHandle] = obj;
            rightMap[obj] = currentHandle;
            return currentHandle;
        }
    }

    public bool TryGet(ulong handle, out T result) {
        // Possible null reference assignment
        #pragma warning disable 8601
        return leftMap.TryGetValue(handle, out result);
        #pragma warning restore 8601
    }

    public bool Remove(ulong handle) {
        return Remove(handle, out T result);
    }

    public bool Remove(ulong handle, out T result) {
        lock (lock_) {
            // Possible null reference assignment
            #pragma warning disable 8601
            if (leftMap.Remove(handle, out result)) {
            #pragma warning restore 8601
                rightMap.Remove(result);
                return true;
            } else {
                return false;
            }
        }
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ForeignCallback(ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf);

internal abstract class FfiConverterCallbackInterface<CallbackInterface>
        : FfiConverter<CallbackInterface, ulong>
        where CallbackInterface: notnull
{
    ConcurrentHandleMap<CallbackInterface> handleMap = new ConcurrentHandleMap<CallbackInterface>();

    // Registers the foreign callback with the Rust side.
    // This method is generated for each callback interface.
    public abstract void Register();

    public RustBuffer Drop(ulong handle) {
        handleMap.Remove(handle);
        return new RustBuffer();
    }

    public override CallbackInterface Lift(ulong handle) {
        if (!handleMap.TryGet(handle, out CallbackInterface result)) {
            throw new InternalException($"No callback in handlemap '{handle}'");
        }
        return result;
    }

    public override CallbackInterface Read(BigEndianStream stream) {
        return Lift(stream.ReadULong());
    }

    public override ulong Lower(CallbackInterface value) {
        return handleMap.Insert(value);
    }

    public override int AllocationSize(CallbackInterface value) {
        return 8;
    }

    public override void Write(CallbackInterface value, BigEndianStream stream) {
        stream.WriteULong(Lower(value));
    }
}
public interface DidResolver {
    ErrorCode Resolve(String @did, OnDidResolverResult @cb);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeDIDResolver {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeDidResolver.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeDidResolver.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeResolve(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return UniffiCallbackConstants.UNEXPECTED_ERROR;
            }
        }
    };

    static RustBuffer InvokeResolve(DidResolver callback, BigEndianStream stream) {var result =callback.Resolve(FfiConverterString.INSTANCE.Read(stream), FfiConverterTypeOnDidResolverResult.INSTANCE.Read(stream));

        return FfiConverterTypeErrorCode.INSTANCE.LowerIntoRustBuffer(result);
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeDidResolver: FfiConverterCallbackInterface<DidResolver> {
    public static FfiConverterTypeDidResolver INSTANCE = new FfiConverterTypeDidResolver();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_DIDResolver_init_callback(ForeignCallbackTypeDIDResolver.INSTANCE, ref status);
        });
    }
}





public interface OnFromPriorPackResult {
    void Success(String @frompriorjwt, String @kid);
    void Error(ErrorKind @err, String @msg);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeOnFromPriorPackResult {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeOnFromPriorPackResult.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeOnFromPriorPackResult.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeSuccess(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            case 2: {
                try {
                    outBuf = InvokeError(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return UniffiCallbackConstants.UNEXPECTED_ERROR;
            }
        }
    };

    static RustBuffer InvokeSuccess(OnFromPriorPackResult callback, BigEndianStream stream) {callback.Success(FfiConverterString.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    static RustBuffer InvokeError(OnFromPriorPackResult callback, BigEndianStream stream) {callback.Error(FfiConverterTypeErrorKind.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeOnFromPriorPackResult: FfiConverterCallbackInterface<OnFromPriorPackResult> {
    public static FfiConverterTypeOnFromPriorPackResult INSTANCE = new FfiConverterTypeOnFromPriorPackResult();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnFromPriorPackResult_init_callback(ForeignCallbackTypeOnFromPriorPackResult.INSTANCE, ref status);
        });
    }
}





public interface OnFromPriorUnpackResult {
    void Success(FromPrior @fromprior, String @kid);
    void Error(ErrorKind @err, String @msg);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeOnFromPriorUnpackResult {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeOnFromPriorUnpackResult.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeOnFromPriorUnpackResult.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeSuccess(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            case 2: {
                try {
                    outBuf = InvokeError(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return UniffiCallbackConstants.UNEXPECTED_ERROR;
            }
        }
    };

    static RustBuffer InvokeSuccess(OnFromPriorUnpackResult callback, BigEndianStream stream) {callback.Success(FfiConverterTypeFromPrior.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    static RustBuffer InvokeError(OnFromPriorUnpackResult callback, BigEndianStream stream) {callback.Error(FfiConverterTypeErrorKind.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeOnFromPriorUnpackResult: FfiConverterCallbackInterface<OnFromPriorUnpackResult> {
    public static FfiConverterTypeOnFromPriorUnpackResult INSTANCE = new FfiConverterTypeOnFromPriorUnpackResult();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnFromPriorUnpackResult_init_callback(ForeignCallbackTypeOnFromPriorUnpackResult.INSTANCE, ref status);
        });
    }
}





public interface OnPackEncryptedResult {
    void Success(String @result, PackEncryptedMetadata @metadata);
    void Error(ErrorKind @err, String @msg);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeOnPackEncryptedResult {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeOnPackEncryptedResult.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeOnPackEncryptedResult.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeSuccess(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            case 2: {
                try {
                    outBuf = InvokeError(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return UniffiCallbackConstants.UNEXPECTED_ERROR;
            }
        }
    };

    static RustBuffer InvokeSuccess(OnPackEncryptedResult callback, BigEndianStream stream) {callback.Success(FfiConverterString.INSTANCE.Read(stream), FfiConverterTypePackEncryptedMetadata.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    static RustBuffer InvokeError(OnPackEncryptedResult callback, BigEndianStream stream) {callback.Error(FfiConverterTypeErrorKind.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeOnPackEncryptedResult: FfiConverterCallbackInterface<OnPackEncryptedResult> {
    public static FfiConverterTypeOnPackEncryptedResult INSTANCE = new FfiConverterTypeOnPackEncryptedResult();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnPackEncryptedResult_init_callback(ForeignCallbackTypeOnPackEncryptedResult.INSTANCE, ref status);
        });
    }
}





public interface OnPackPlaintextResult {
    void Success(String @result);
    void Error(ErrorKind @err, String @msg);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeOnPackPlaintextResult {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeOnPackPlaintextResult.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeOnPackPlaintextResult.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeSuccess(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            case 2: {
                try {
                    outBuf = InvokeError(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return UniffiCallbackConstants.UNEXPECTED_ERROR;
            }
        }
    };

    static RustBuffer InvokeSuccess(OnPackPlaintextResult callback, BigEndianStream stream) {callback.Success(FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    static RustBuffer InvokeError(OnPackPlaintextResult callback, BigEndianStream stream) {callback.Error(FfiConverterTypeErrorKind.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeOnPackPlaintextResult: FfiConverterCallbackInterface<OnPackPlaintextResult> {
    public static FfiConverterTypeOnPackPlaintextResult INSTANCE = new FfiConverterTypeOnPackPlaintextResult();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnPackPlaintextResult_init_callback(ForeignCallbackTypeOnPackPlaintextResult.INSTANCE, ref status);
        });
    }
}





public interface OnPackSignedResult {
    void Success(String @result, PackSignedMetadata @metadata);
    void Error(ErrorKind @err, String @msg);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeOnPackSignedResult {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeOnPackSignedResult.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeOnPackSignedResult.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeSuccess(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            case 2: {
                try {
                    outBuf = InvokeError(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return UniffiCallbackConstants.UNEXPECTED_ERROR;
            }
        }
    };

    static RustBuffer InvokeSuccess(OnPackSignedResult callback, BigEndianStream stream) {callback.Success(FfiConverterString.INSTANCE.Read(stream), FfiConverterTypePackSignedMetadata.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    static RustBuffer InvokeError(OnPackSignedResult callback, BigEndianStream stream) {callback.Error(FfiConverterTypeErrorKind.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeOnPackSignedResult: FfiConverterCallbackInterface<OnPackSignedResult> {
    public static FfiConverterTypeOnPackSignedResult INSTANCE = new FfiConverterTypeOnPackSignedResult();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnPackSignedResult_init_callback(ForeignCallbackTypeOnPackSignedResult.INSTANCE, ref status);
        });
    }
}





public interface OnUnpackResult {
    void Success(Message @result, UnpackMetadata @metadata);
    void Error(ErrorKind @err, String @msg);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeOnUnpackResult {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeOnUnpackResult.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeOnUnpackResult.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeSuccess(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            case 2: {
                try {
                    outBuf = InvokeError(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return UniffiCallbackConstants.UNEXPECTED_ERROR;
            }
        }
    };

    static RustBuffer InvokeSuccess(OnUnpackResult callback, BigEndianStream stream) {callback.Success(FfiConverterTypeMessage.INSTANCE.Read(stream), FfiConverterTypeUnpackMetadata.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    static RustBuffer InvokeError(OnUnpackResult callback, BigEndianStream stream) {callback.Error(FfiConverterTypeErrorKind.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeOnUnpackResult: FfiConverterCallbackInterface<OnUnpackResult> {
    public static FfiConverterTypeOnUnpackResult INSTANCE = new FfiConverterTypeOnUnpackResult();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnUnpackResult_init_callback(ForeignCallbackTypeOnUnpackResult.INSTANCE, ref status);
        });
    }
}





public interface OnWrapInForwardResult {
    void Success(String @result);
    void Error(ErrorKind @err, String @msg);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeOnWrapInForwardResult {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeOnWrapInForwardResult.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeOnWrapInForwardResult.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeSuccess(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            case 2: {
                try {
                    outBuf = InvokeError(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return UniffiCallbackConstants.UNEXPECTED_ERROR;
            }
        }
    };

    static RustBuffer InvokeSuccess(OnWrapInForwardResult callback, BigEndianStream stream) {callback.Success(FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    static RustBuffer InvokeError(OnWrapInForwardResult callback, BigEndianStream stream) {callback.Error(FfiConverterTypeErrorKind.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));

        return new RustBuffer();
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeOnWrapInForwardResult: FfiConverterCallbackInterface<OnWrapInForwardResult> {
    public static FfiConverterTypeOnWrapInForwardResult INSTANCE = new FfiConverterTypeOnWrapInForwardResult();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_OnWrapInForwardResult_init_callback(ForeignCallbackTypeOnWrapInForwardResult.INSTANCE, ref status);
        });
    }
}





public interface SecretsResolver {
    ErrorCode GetSecret(String @secretid, OnGetSecretResult @cb);
    ErrorCode FindSecrets(List<String> @secretids, OnFindSecretsResult @cb);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeSecretsResolver {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, IntPtr argsData, int argsLength, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeSecretsResolver.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeSecretsResolver.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeGetSecret(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            case 2: {
                try {
                    outBuf = InvokeFindSecrets(cb, RustBuffer.MemoryStream(argsData, argsLength));
                    return UniffiCallbackConstants.SUCCESS;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return UniffiCallbackConstants.UNEXPECTED_ERROR;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return UniffiCallbackConstants.UNEXPECTED_ERROR;
            }
        }
    };

    static RustBuffer InvokeGetSecret(SecretsResolver callback, BigEndianStream stream) {var result =callback.GetSecret(FfiConverterString.INSTANCE.Read(stream), FfiConverterTypeOnGetSecretResult.INSTANCE.Read(stream));

        return FfiConverterTypeErrorCode.INSTANCE.LowerIntoRustBuffer(result);
    }

    static RustBuffer InvokeFindSecrets(SecretsResolver callback, BigEndianStream stream) {var result =callback.FindSecrets(FfiConverterSequenceString.INSTANCE.Read(stream), FfiConverterTypeOnFindSecretsResult.INSTANCE.Read(stream));

        return FfiConverterTypeErrorCode.INSTANCE.LowerIntoRustBuffer(result);
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeSecretsResolver: FfiConverterCallbackInterface<SecretsResolver> {
    public static FfiConverterTypeSecretsResolver INSTANCE = new FfiConverterTypeSecretsResolver();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_didcomm_f8e5_SecretsResolver_init_callback(ForeignCallbackTypeSecretsResolver.INSTANCE, ref status);
        });
    }
}




class FfiConverterOptionalULong: FfiConverterRustBuffer<UInt64?> {
    public static FfiConverterOptionalULong INSTANCE = new FfiConverterOptionalULong();

    public override UInt64? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterULong.INSTANCE.Read(stream);
    }

    public override int AllocationSize(UInt64? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterULong.INSTANCE.AllocationSize((UInt64)value);
        }
    }

    public override void Write(UInt64? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterULong.INSTANCE.Write((UInt64)value, stream);
        }
    }
}




class FfiConverterOptionalString: FfiConverterRustBuffer<String?> {
    public static FfiConverterOptionalString INSTANCE = new FfiConverterOptionalString();

    public override String? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterString.INSTANCE.Read(stream);
    }

    public override int AllocationSize(String? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterString.INSTANCE.AllocationSize((String)value);
        }
    }

    public override void Write(String? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterString.INSTANCE.Write((String)value, stream);
        }
    }
}




class FfiConverterOptionalTypeDidDoc: FfiConverterRustBuffer<DidDoc?> {
    public static FfiConverterOptionalTypeDidDoc INSTANCE = new FfiConverterOptionalTypeDidDoc();

    public override DidDoc? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterTypeDidDoc.INSTANCE.Read(stream);
    }

    public override int AllocationSize(DidDoc? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterTypeDidDoc.INSTANCE.AllocationSize((DidDoc)value);
        }
    }

    public override void Write(DidDoc? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterTypeDidDoc.INSTANCE.Write((DidDoc)value, stream);
        }
    }
}




class FfiConverterOptionalTypeFromPrior: FfiConverterRustBuffer<FromPrior?> {
    public static FfiConverterOptionalTypeFromPrior INSTANCE = new FfiConverterOptionalTypeFromPrior();

    public override FromPrior? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterTypeFromPrior.INSTANCE.Read(stream);
    }

    public override int AllocationSize(FromPrior? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterTypeFromPrior.INSTANCE.AllocationSize((FromPrior)value);
        }
    }

    public override void Write(FromPrior? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterTypeFromPrior.INSTANCE.Write((FromPrior)value, stream);
        }
    }
}




class FfiConverterOptionalTypeMessagingServiceMetadata: FfiConverterRustBuffer<MessagingServiceMetadata?> {
    public static FfiConverterOptionalTypeMessagingServiceMetadata INSTANCE = new FfiConverterOptionalTypeMessagingServiceMetadata();

    public override MessagingServiceMetadata? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterTypeMessagingServiceMetadata.INSTANCE.Read(stream);
    }

    public override int AllocationSize(MessagingServiceMetadata? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterTypeMessagingServiceMetadata.INSTANCE.AllocationSize((MessagingServiceMetadata)value);
        }
    }

    public override void Write(MessagingServiceMetadata? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterTypeMessagingServiceMetadata.INSTANCE.Write((MessagingServiceMetadata)value, stream);
        }
    }
}




class FfiConverterOptionalTypeSecret: FfiConverterRustBuffer<Secret?> {
    public static FfiConverterOptionalTypeSecret INSTANCE = new FfiConverterOptionalTypeSecret();

    public override Secret? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterTypeSecret.INSTANCE.Read(stream);
    }

    public override int AllocationSize(Secret? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterTypeSecret.INSTANCE.AllocationSize((Secret)value);
        }
    }

    public override void Write(Secret? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterTypeSecret.INSTANCE.Write((Secret)value, stream);
        }
    }
}




class FfiConverterOptionalTypeAnonCryptAlg: FfiConverterRustBuffer<AnonCryptAlg?> {
    public static FfiConverterOptionalTypeAnonCryptAlg INSTANCE = new FfiConverterOptionalTypeAnonCryptAlg();

    public override AnonCryptAlg? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterTypeAnonCryptAlg.INSTANCE.Read(stream);
    }

    public override int AllocationSize(AnonCryptAlg? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterTypeAnonCryptAlg.INSTANCE.AllocationSize((AnonCryptAlg)value);
        }
    }

    public override void Write(AnonCryptAlg? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterTypeAnonCryptAlg.INSTANCE.Write((AnonCryptAlg)value, stream);
        }
    }
}




class FfiConverterOptionalTypeAuthCryptAlg: FfiConverterRustBuffer<AuthCryptAlg?> {
    public static FfiConverterOptionalTypeAuthCryptAlg INSTANCE = new FfiConverterOptionalTypeAuthCryptAlg();

    public override AuthCryptAlg? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterTypeAuthCryptAlg.INSTANCE.Read(stream);
    }

    public override int AllocationSize(AuthCryptAlg? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterTypeAuthCryptAlg.INSTANCE.AllocationSize((AuthCryptAlg)value);
        }
    }

    public override void Write(AuthCryptAlg? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterTypeAuthCryptAlg.INSTANCE.Write((AuthCryptAlg)value, stream);
        }
    }
}




class FfiConverterOptionalTypeSignAlg: FfiConverterRustBuffer<SignAlg?> {
    public static FfiConverterOptionalTypeSignAlg INSTANCE = new FfiConverterOptionalTypeSignAlg();

    public override SignAlg? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterTypeSignAlg.INSTANCE.Read(stream);
    }

    public override int AllocationSize(SignAlg? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterTypeSignAlg.INSTANCE.AllocationSize((SignAlg)value);
        }
    }

    public override void Write(SignAlg? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterTypeSignAlg.INSTANCE.Write((SignAlg)value, stream);
        }
    }
}




class FfiConverterOptionalSequenceString: FfiConverterRustBuffer<List<String>?> {
    public static FfiConverterOptionalSequenceString INSTANCE = new FfiConverterOptionalSequenceString();

    public override List<String>? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterSequenceString.INSTANCE.Read(stream);
    }

    public override int AllocationSize(List<String>? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterSequenceString.INSTANCE.AllocationSize((List<String>)value);
        }
    }

    public override void Write(List<String>? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterSequenceString.INSTANCE.Write((List<String>)value, stream);
        }
    }
}




class FfiConverterOptionalSequenceTypeAttachment: FfiConverterRustBuffer<List<Attachment>?> {
    public static FfiConverterOptionalSequenceTypeAttachment INSTANCE = new FfiConverterOptionalSequenceTypeAttachment();

    public override List<Attachment>? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterSequenceTypeAttachment.INSTANCE.Read(stream);
    }

    public override int AllocationSize(List<Attachment>? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterSequenceTypeAttachment.INSTANCE.AllocationSize((List<Attachment>)value);
        }
    }

    public override void Write(List<Attachment>? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterSequenceTypeAttachment.INSTANCE.Write((List<Attachment>)value, stream);
        }
    }
}




class FfiConverterOptionalDictionaryStringJsonValue: FfiConverterRustBuffer<Dictionary<String, JsonValue>?> {
    public static FfiConverterOptionalDictionaryStringJsonValue INSTANCE = new FfiConverterOptionalDictionaryStringJsonValue();

    public override Dictionary<String, JsonValue>? Read(BigEndianStream stream) {
        if (stream.ReadByte() == 0) {
            return null;
        }
        return FfiConverterDictionaryStringJsonValue.INSTANCE.Read(stream);
    }

    public override int AllocationSize(Dictionary<String, JsonValue>? value) {
        if (value == null) {
            return 1;
        } else {
            return 1 + FfiConverterDictionaryStringJsonValue.INSTANCE.AllocationSize((Dictionary<String, JsonValue>)value);
        }
    }

    public override void Write(Dictionary<String, JsonValue>? value, BigEndianStream stream) {
        if (value == null) {
            stream.WriteByte(0);
        } else {
            stream.WriteByte(1);
            FfiConverterDictionaryStringJsonValue.INSTANCE.Write((Dictionary<String, JsonValue>)value, stream);
        }
    }
}




class FfiConverterSequenceString: FfiConverterRustBuffer<List<String>> {
    public static FfiConverterSequenceString INSTANCE = new FfiConverterSequenceString();

    public override List<String> Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var result = new List<String>(length);
        for (int i = 0; i < length; i++) {
            result.Add(FfiConverterString.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<String> value) {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterString.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<String> value, BigEndianStream stream) {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterString.INSTANCE.Write(item, stream));
    }
}




class FfiConverterSequenceTypeAttachment: FfiConverterRustBuffer<List<Attachment>> {
    public static FfiConverterSequenceTypeAttachment INSTANCE = new FfiConverterSequenceTypeAttachment();

    public override List<Attachment> Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var result = new List<Attachment>(length);
        for (int i = 0; i < length; i++) {
            result.Add(FfiConverterTypeAttachment.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<Attachment> value) {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterTypeAttachment.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<Attachment> value, BigEndianStream stream) {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterTypeAttachment.INSTANCE.Write(item, stream));
    }
}




class FfiConverterSequenceTypeDidDoc: FfiConverterRustBuffer<List<DidDoc>> {
    public static FfiConverterSequenceTypeDidDoc INSTANCE = new FfiConverterSequenceTypeDidDoc();

    public override List<DidDoc> Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var result = new List<DidDoc>(length);
        for (int i = 0; i < length; i++) {
            result.Add(FfiConverterTypeDidDoc.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<DidDoc> value) {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterTypeDidDoc.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<DidDoc> value, BigEndianStream stream) {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterTypeDidDoc.INSTANCE.Write(item, stream));
    }
}




class FfiConverterSequenceTypeSecret: FfiConverterRustBuffer<List<Secret>> {
    public static FfiConverterSequenceTypeSecret INSTANCE = new FfiConverterSequenceTypeSecret();

    public override List<Secret> Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var result = new List<Secret>(length);
        for (int i = 0; i < length; i++) {
            result.Add(FfiConverterTypeSecret.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<Secret> value) {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterTypeSecret.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<Secret> value, BigEndianStream stream) {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterTypeSecret.INSTANCE.Write(item, stream));
    }
}




class FfiConverterSequenceTypeService: FfiConverterRustBuffer<List<Service>> {
    public static FfiConverterSequenceTypeService INSTANCE = new FfiConverterSequenceTypeService();

    public override List<Service> Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var result = new List<Service>(length);
        for (int i = 0; i < length; i++) {
            result.Add(FfiConverterTypeService.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<Service> value) {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterTypeService.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<Service> value, BigEndianStream stream) {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterTypeService.INSTANCE.Write(item, stream));
    }
}




class FfiConverterSequenceTypeVerificationMethod: FfiConverterRustBuffer<List<VerificationMethod>> {
    public static FfiConverterSequenceTypeVerificationMethod INSTANCE = new FfiConverterSequenceTypeVerificationMethod();

    public override List<VerificationMethod> Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var result = new List<VerificationMethod>(length);
        for (int i = 0; i < length; i++) {
            result.Add(FfiConverterTypeVerificationMethod.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<VerificationMethod> value) {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterTypeVerificationMethod.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<VerificationMethod> value, BigEndianStream stream) {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterTypeVerificationMethod.INSTANCE.Write(item, stream));
    }
}



class FfiConverterDictionaryStringJsonValue: FfiConverterRustBuffer<Dictionary<String, JsonValue>> {
    public static FfiConverterDictionaryStringJsonValue INSTANCE = new FfiConverterDictionaryStringJsonValue();

    public override Dictionary<String, JsonValue> Read(BigEndianStream stream) {
        var result = new Dictionary<String, JsonValue>();
        var len = stream.ReadInt();
        for (int i = 0; i < len; i++) {
            var key = FfiConverterString.INSTANCE.Read(stream);
            var value = FfiConverterTypeJsonValue.INSTANCE.Read(stream);
            result[key] = value;
        }
        return result;
    }

    public override int AllocationSize(Dictionary<String, JsonValue> value) {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => {
            return FfiConverterString.INSTANCE.AllocationSize(item.Key) +
                FfiConverterTypeJsonValue.INSTANCE.AllocationSize(item.Value);
        }).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(Dictionary<String, JsonValue> value, BigEndianStream stream) {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null) {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        foreach (var item in value) {
            FfiConverterString.INSTANCE.Write(item.Key, stream);
            FfiConverterTypeJsonValue.INSTANCE.Write(item.Value, stream);
        }
    }
}



/**
 * Typealias from the type name used in the UDL file to the builtin type.  This
 * is needed because the UDL type name is used in function/method signatures.
 * It's also what we have an external type that references a custom type.
 */
#pragma warning restore 8625

public static class DidcommMethods {
}

