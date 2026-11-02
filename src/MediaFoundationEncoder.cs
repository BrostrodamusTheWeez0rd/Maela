using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Maela
{
    /// <summary>Something that turns I420 frames into H.264.</summary>
    internal interface IVideoEncoder : IDisposable
    {
        int Width { get; }
        int Height { get; }
        string Name { get; }
        void ForceKeyframe();
        /// <summary>Changes the bitrate without restarting. False if this encoder can't.</summary>
        bool SetBitrate(int kbps);
        /// <summary>Returns Annex B bytes, or null if no frame came out this time.</summary>
        byte[] Encode(I420Frame frame, long timestampMs, out bool keyframe);
    }

    internal static class VideoEncoderFactory
    {
        /// <summary>The graphics card's encoder if there's a working one, otherwise OpenH264 on the processor.</summary>
        public static IVideoEncoder Create(int width, int height, int fps, int kbps, bool allowHardware)
        {
            if (allowHardware)
            {
                IVideoEncoder hardware = MediaFoundationEncoder.TryCreate(width, height, fps, kbps);
                if (hardware != null)
                    return hardware;
            }
            return new H264Encoder(width, height, fps, kbps);
        }
    }

    /// <summary>
    /// H.264 on the graphics card (NVIDIA, AMD or Intel) through Windows Media Foundation, using
    /// hardware encoders only. Video stays constrained baseline, the same kind OpenH264 makes,
    /// so every viewer can decode it. Only encodes this PC's own screen.
    /// </summary>
    internal sealed class MediaFoundationEncoder : IVideoEncoder
    {
        private const int MF_VERSION = 0x00020070, MFSTARTUP_LITE = 1;
        private const int MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
        private const int MFT_MESSAGE_COMMAND_FLUSH = 0, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001, MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
        private const int METransformNeedInput = 601, METransformHaveOutput = 602;
        private const int MF_EVENT_FLAG_NO_WAIT = 1;
        private const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private const int MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100, MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;
        private const int VT_UI4 = 19, VT_BOOL = 11;
        private const int InputTimeoutMs = 1000, OutputWaitMs = 40;

        private static Guid MFT_CATEGORY_VIDEO_ENCODER = new Guid("f79eac7d-e545-4387-bdee-d647d7bde42a");
        private static Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        private static Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        private static Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        private static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static Guid MF_MT_MPEG2_PROFILE = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        private static Guid MF_MT_MPEG_SEQUENCE_HEADER = new Guid("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
        private static Guid MF_TRANSFORM_ASYNC_UNLOCK = new Guid("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        private static Guid MF_LOW_LATENCY = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        private static Guid MFT_FRIENDLY_NAME = new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3");
        private static Guid CODECAPI_RateControlMode = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");
        private static Guid CODECAPI_MeanBitRate = new Guid("f7222374-2144-4815-b550-a37f8e12ee52");
        private static Guid CODECAPI_GOPSize = new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");
        private static Guid CODECAPI_BPictureCount = new Guid("8d390aac-dc5c-4200-b57f-814d04babab2");
        private static Guid CODECAPI_LowLatencyMode = new Guid("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        private static Guid CODECAPI_ForceKeyFrame = new Guid("398c1b98-8353-475a-9ef2-8f265d260345");
        private static Guid IID_IMFTransform = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");
        private static Guid IID_IMFMediaEventGenerator = new Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D");
        private static Guid IID_ICodecAPI = new Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

        [StructLayout(LayoutKind.Sequential)] private struct RegisterTypeInfo { public Guid Major; public Guid Sub; }
        [StructLayout(LayoutKind.Sequential)] private struct OutputStreamInfo { public int Flags, Size, Alignment; }
        [StructLayout(LayoutKind.Sequential)] private struct OutputDataBuffer { public int StreamId; public IntPtr Sample; public int Status; public IntPtr Events; }

        [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] private static extern int MFShutdown();
        [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, int flags, IntPtr input, ref RegisterTypeInfo output, out IntPtr activates, out int count);
        [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(int length, out IntPtr buffer);
        [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr memory);

        private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);
        private delegate int ReleaseFn(IntPtr self);
        private delegate int SetUInt32Fn(IntPtr self, ref Guid key, int value);
        private delegate int SetUInt64Fn(IntPtr self, ref Guid key, long value);
        private delegate int SetGuidFn(IntPtr self, ref Guid key, ref Guid value);
        private delegate int GetAllocatedStringFn(IntPtr self, ref Guid key, out IntPtr text, out int length);
        private delegate int GetBlobSizeFn(IntPtr self, ref Guid key, out int size);
        private delegate int GetBlobFn(IntPtr self, ref Guid key, byte[] blob, int size, out int written);
        private delegate int ActivateObjectFn(IntPtr self, ref Guid iid, out IntPtr result);
        private delegate int NoArgsFn(IntPtr self);
        private delegate int GetAttributesFn(IntPtr self, out IntPtr attributes);
        private delegate int GetStreamIdsFn(IntPtr self, int inputSize, int[] inputs, int outputSize, int[] outputs);
        private delegate int GetOutputStreamInfoFn(IntPtr self, int stream, out OutputStreamInfo info);
        private delegate int SetTypeFn(IntPtr self, int stream, IntPtr type, int flags);
        private delegate int GetTypeFn(IntPtr self, int stream, out IntPtr type);
        private delegate int ProcessMessageFn(IntPtr self, int message, IntPtr parameter);
        private delegate int ProcessInputFn(IntPtr self, int stream, IntPtr sample, int flags);
        private delegate int ProcessOutputFn(IntPtr self, int flags, int count, ref OutputDataBuffer buffer, out int status);
        private delegate int GetEventFn(IntPtr self, int flags, out IntPtr mediaEvent);
        private delegate int GetEventTypeFn(IntPtr self, out int type);
        private delegate int SetValueFn(IntPtr self, ref Guid api, IntPtr value);
        private delegate int SetSampleLongFn(IntPtr self, long value);
        private delegate int AddBufferFn(IntPtr self, IntPtr buffer);
        private delegate int ConvertToContiguousFn(IntPtr self, out IntPtr buffer);
        private delegate int LockFn(IntPtr self, out IntPtr data, out int max, out int current);
        private delegate int SetLengthFn(IntPtr self, int length);

        // Slots in the COM tables (0-2 IUnknown; IMFAttributes methods run 3-32).
        private const int AttrGetBlobSize = 14, AttrGetBlob = 15, AttrGetAllocatedString = 13, AttrSetUInt32 = 21, AttrSetUInt64 = 22, AttrSetGuid = 24;
        private const int ActivateObject = 33, ActivateShutdown = 34;
        private const int XformGetStreamIds = 5, XformGetOutputInfo = 7, XformGetAttributes = 8, XformSetInput = 15, XformSetOutput = 16, XformGetOutputCurrent = 18, XformMessage = 23, XformInput = 24, XformOutput = 25;
        private const int EventGenGet = 3, EventGetType = 33;
        private const int SampleSetTime = 36, SampleSetDuration = 38, SampleConvert = 41, SampleAddBuffer = 42;
        private const int BufferLock = 3, BufferUnlock = 4, BufferSetLength = 6;
        private const int CodecSetValue = 9;

        private static T Slot<T>(IntPtr com, int index) where T : class
        {
            IntPtr table = Marshal.ReadIntPtr(com);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, index * IntPtr.Size), typeof(T));
        }

        private static void Release(IntPtr com)
        {
            if (com != IntPtr.Zero)
                Slot<ReleaseFn>(com, 2)(com);
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0)
                throw new InvalidOperationException(what + " failed (0x" + hr.ToString("X8") + ")");
        }

        private readonly int _width, _height, _fps;
        private readonly Queue<byte[]> _outputs = new Queue<byte[]>();
        private IntPtr _activate, _transform, _events, _codec, _variant;
        private ProcessInputFn _processInput;
        private ProcessOutputFn _processOutput;
        private GetEventFn _getEvent;
        private SetValueFn _setValue;
        private int _inputStream, _outputStream, _outputFlags, _outputSize, _needInput;
        private byte[] _sequenceHeader;
        private string _name = "Graphics card";
        private bool _started, _disposed;

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }
        public string Name { get { return _name; } }

        /// <summary>Null if this PC has no hardware H.264 encoder that accepts these settings.</summary>
        public static MediaFoundationEncoder TryCreate(int width, int height, int fps, int kbps)
        {
            if (IntPtr.Size != 8)
                return null;
            int started;
            try { started = MFStartup(MF_VERSION, MFSTARTUP_LITE); }
            catch { return null; } // Windows without Media Foundation (some "N" editions).
            if (started < 0)
                return null;

            IntPtr activates = IntPtr.Zero;
            int count = 0;
            try
            {
                RegisterTypeInfo output = new RegisterTypeInfo();
                output.Major = MFMediaType_Video;
                output.Sub = MFVideoFormat_H264;
                if (MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, IntPtr.Zero, ref output, out activates, out count) < 0)
                    count = 0;
                MediaFoundationEncoder chosen = null;
                for (int i = 0; i < count; i++)
                {
                    IntPtr activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                    if (chosen == null)
                    {
                        MediaFoundationEncoder candidate = new MediaFoundationEncoder(width, height, fps);
                        try
                        {
                            candidate.Open(activate, kbps);
                            chosen = candidate;
                            continue; // It keeps this one's reference.
                        }
                        catch
                        {
                            candidate._activate = IntPtr.Zero; // Still ours to release, below.
                            candidate.Dispose();
                            // Frees the graphics card's encoder session if it got as far as making one.
                            try { Slot<NoArgsFn>(activate, ActivateShutdown)(activate); } catch { }
                        }
                    }
                    Release(activate);
                }
                if (chosen == null)
                    MFShutdown();
                return chosen;
            }
            finally
            {
                if (activates != IntPtr.Zero)
                    CoTaskMemFree(activates);
            }
        }

        private MediaFoundationEncoder(int width, int height, int fps)
        {
            _width = width & ~1;
            _height = height & ~1;
            _fps = fps;
            _variant = Marshal.AllocHGlobal(24);
        }

        private void Open(IntPtr activate, int kbps)
        {
            _activate = activate;
            IntPtr text;
            int length;
            if (Slot<GetAllocatedStringFn>(activate, AttrGetAllocatedString)(activate, ref MFT_FRIENDLY_NAME, out text, out length) >= 0)
            {
                _name = Marshal.PtrToStringUni(text, length);
                CoTaskMemFree(text);
            }

            Guid iid = IID_IMFTransform;
            Check(Slot<ActivateObjectFn>(activate, ActivateObject)(activate, ref iid, out _transform), "ActivateObject");
            IntPtr attributes;
            Check(Slot<GetAttributesFn>(_transform, XformGetAttributes)(_transform, out attributes), "GetAttributes");
            try
            {
                Check(Slot<SetUInt32Fn>(attributes, AttrSetUInt32)(attributes, ref MF_TRANSFORM_ASYNC_UNLOCK, 1), "Unlock");
                Slot<SetUInt32Fn>(attributes, AttrSetUInt32)(attributes, ref MF_LOW_LATENCY, 1);
            }
            finally { Release(attributes); }

            iid = IID_IMFMediaEventGenerator;
            Check(Slot<QueryInterfaceFn>(_transform, 0)(_transform, ref iid, out _events), "Event generator");
            iid = IID_ICodecAPI;
            Check(Slot<QueryInterfaceFn>(_transform, 0)(_transform, ref iid, out _codec), "ICodecAPI");
            _setValue = Slot<SetValueFn>(_codec, CodecSetValue);
            _getEvent = Slot<GetEventFn>(_events, EventGenGet);
            _processInput = Slot<ProcessInputFn>(_transform, XformInput);
            _processOutput = Slot<ProcessOutputFn>(_transform, XformOutput);

            int[] inputs = new int[1], outputs = new int[1];
            int ids = Slot<GetStreamIdsFn>(_transform, XformGetStreamIds)(_transform, 1, inputs, 1, outputs);
            if (ids >= 0)
            {
                _inputStream = inputs[0];
                _outputStream = outputs[0];
            }
            else if (ids != E_NOTIMPL)
                Check(ids, "GetStreamIDs");

            // Not every encoder takes every setting, and none is essential.
            SetCodecBool(ref CODECAPI_LowLatencyMode, true);
            SetCodecUInt(ref CODECAPI_RateControlMode, 0); // Constant bitrate.
            SetCodecUInt(ref CODECAPI_MeanBitRate, kbps * 1000);
            SetCodecUInt(ref CODECAPI_BPictureCount, 0);
            SetCodecUInt(ref CODECAPI_GOPSize, 0); // Keyframes only when asked.

            IntPtr outType = NewVideoType(ref MFVideoFormat_H264);
            try
            {
                Slot<SetUInt32Fn>(outType, AttrSetUInt32)(outType, ref MF_MT_AVG_BITRATE, kbps * 1000);
                Slot<SetUInt32Fn>(outType, AttrSetUInt32)(outType, ref MF_MT_MPEG2_PROFILE, 66); // Baseline.
                Check(Slot<SetTypeFn>(_transform, XformSetOutput)(_transform, _outputStream, outType, 0), "SetOutputType");
            }
            finally { Release(outType); }

            IntPtr inType = NewVideoType(ref MFVideoFormat_NV12);
            try { Check(Slot<SetTypeFn>(_transform, XformSetInput)(_transform, _inputStream, inType, 0), "SetInputType"); }
            finally { Release(inType); }

            ReadSequenceHeader();
            OutputStreamInfo info;
            Check(Slot<GetOutputStreamInfoFn>(_transform, XformGetOutputInfo)(_transform, _outputStream, out info), "GetOutputStreamInfo");
            _outputFlags = info.Flags;
            _outputSize = Math.Max(info.Size, _width * _height);

            ProcessMessageFn message = Slot<ProcessMessageFn>(_transform, XformMessage);
            Check(message(_transform, MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero), "Flush");
            Check(message(_transform, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "Begin streaming");
            Check(message(_transform, MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "Start of stream");
            _started = true;
        }

        private IntPtr NewVideoType(ref Guid subtype)
        {
            IntPtr type;
            Check(MFCreateMediaType(out type), "MFCreateMediaType");
            Slot<SetGuidFn>(type, AttrSetGuid)(type, ref MF_MT_MAJOR_TYPE, ref MFMediaType_Video);
            Slot<SetGuidFn>(type, AttrSetGuid)(type, ref MF_MT_SUBTYPE, ref subtype);
            Slot<SetUInt64Fn>(type, AttrSetUInt64)(type, ref MF_MT_FRAME_SIZE, ((long)_width << 32) | (uint)_height);
            Slot<SetUInt64Fn>(type, AttrSetUInt64)(type, ref MF_MT_FRAME_RATE, ((long)_fps << 32) | 1L);
            Slot<SetUInt64Fn>(type, AttrSetUInt64)(type, ref MF_MT_PIXEL_ASPECT_RATIO, (1L << 32) | 1L);
            Slot<SetUInt32Fn>(type, AttrSetUInt32)(type, ref MF_MT_INTERLACE_MODE, 2); // Progressive.
            return type;
        }

        private void ReadSequenceHeader()
        {
            IntPtr current;
            if (Slot<GetTypeFn>(_transform, XformGetOutputCurrent)(_transform, _outputStream, out current) < 0)
                return;
            try
            {
                int size;
                if (Slot<GetBlobSizeFn>(current, AttrGetBlobSize)(current, ref MF_MT_MPEG_SEQUENCE_HEADER, out size) < 0 || size <= 0 || size > 4096)
                    return;
                byte[] blob = new byte[size];
                int written;
                if (Slot<GetBlobFn>(current, AttrGetBlob)(current, ref MF_MT_MPEG_SEQUENCE_HEADER, blob, size, out written) >= 0)
                    _sequenceHeader = blob;
            }
            finally { Release(current); }
        }

        private bool SetCodecUInt(ref Guid api, int value)
        {
            Marshal.WriteInt64(_variant, 0, 0);
            Marshal.WriteInt64(_variant, 8, 0);
            Marshal.WriteInt64(_variant, 16, 0);
            Marshal.WriteInt16(_variant, 0, (short)VT_UI4);
            Marshal.WriteInt32(_variant, 8, value);
            return _setValue(_codec, ref api, _variant) >= 0;
        }

        private bool SetCodecBool(ref Guid api, bool value)
        {
            Marshal.WriteInt64(_variant, 0, 0);
            Marshal.WriteInt64(_variant, 8, 0);
            Marshal.WriteInt64(_variant, 16, 0);
            Marshal.WriteInt16(_variant, 0, (short)VT_BOOL);
            Marshal.WriteInt16(_variant, 8, value ? (short)-1 : (short)0);
            return _setValue(_codec, ref api, _variant) >= 0;
        }

        public void ForceKeyframe()
        {
            if (!_disposed)
                SetCodecUInt(ref CODECAPI_ForceKeyFrame, 1);
        }

        public bool SetBitrate(int kbps)
        {
            return !_disposed && SetCodecUInt(ref CODECAPI_MeanBitRate, kbps * 1000);
        }

        public byte[] Encode(I420Frame frame, long timestampMs, out bool keyframe)
        {
            keyframe = false;
            if (_disposed || frame.Width != _width || frame.Height != _height)
                return null;

            Stopwatch wait = Stopwatch.StartNew();
            PumpEvents();
            while (_needInput == 0)
            {
                if (wait.ElapsedMilliseconds > InputTimeoutMs)
                    throw new InvalidOperationException("The graphics card stopped accepting frames.");
                Thread.Sleep(1);
                PumpEvents();
            }

            IntPtr sample = MakeSample(frame, timestampMs);
            try
            {
                int hr = _processInput(_transform, _inputStream, sample, 0);
                Check(hr, "ProcessInput");
                _needInput--;
            }
            finally { Release(sample); }

            wait.Restart();
            PumpEvents();
            while (_outputs.Count == 0 && wait.ElapsedMilliseconds < OutputWaitMs)
            {
                Thread.Sleep(1);
                PumpEvents();
            }
            if (_outputs.Count == 0)
                return null;
            byte[] data = _outputs.Dequeue();
            keyframe = HasNal(data, 5);
            if (keyframe && !HasNal(data, 7) && _sequenceHeader != null)
            {
                // Some encoders keep SPS/PPS apart; viewers need them with each keyframe.
                byte[] joined = new byte[_sequenceHeader.Length + data.Length];
                Buffer.BlockCopy(_sequenceHeader, 0, joined, 0, _sequenceHeader.Length);
                Buffer.BlockCopy(data, 0, joined, _sequenceHeader.Length, data.Length);
                data = joined;
            }
            return data;
        }

        private void PumpEvents()
        {
            while (true)
            {
                IntPtr mediaEvent;
                int hr = _getEvent(_events, MF_EVENT_FLAG_NO_WAIT, out mediaEvent);
                if (hr == MF_E_NO_EVENTS_AVAILABLE)
                    return;
                Check(hr, "GetEvent");
                int type;
                try { Check(Slot<GetEventTypeFn>(mediaEvent, EventGetType)(mediaEvent, out type), "GetEvent type"); }
                finally { Release(mediaEvent); }
                if (type == METransformNeedInput)
                    _needInput++;
                else if (type == METransformHaveOutput)
                    ReadOutput();
            }
        }

        private unsafe IntPtr MakeSample(I420Frame frame, long timestampMs)
        {
            int size = _width * _height * 3 / 2;
            IntPtr sample, buffer;
            Check(MFCreateSample(out sample), "MFCreateSample");
            Check(MFCreateMemoryBuffer(size, out buffer), "MFCreateMemoryBuffer");
            try
            {
                IntPtr data;
                int max, current;
                Check(Slot<LockFn>(buffer, BufferLock)(buffer, out data, out max, out current), "Lock");
                try
                {
                    Marshal.Copy(frame.Y, 0, data, _width * _height);
                    byte* uv = (byte*)data + _width * _height;
                    int chroma = (_width / 2) * (_height / 2);
                    fixed (byte* u = frame.U, v = frame.V)
                    {
                        for (int i = 0; i < chroma; i++)
                        {
                            uv[i * 2] = u[i];
                            uv[i * 2 + 1] = v[i];
                        }
                    }
                }
                finally { Slot<NoArgsFn>(buffer, BufferUnlock)(buffer); }
                Check(Slot<SetLengthFn>(buffer, BufferSetLength)(buffer, size), "SetCurrentLength");
                Check(Slot<AddBufferFn>(sample, SampleAddBuffer)(sample, buffer), "AddBuffer");
                Slot<SetSampleLongFn>(sample, SampleSetTime)(sample, timestampMs * 10000);
                Slot<SetSampleLongFn>(sample, SampleSetDuration)(sample, 10000000L / _fps);
            }
            catch
            {
                Release(sample);
                throw;
            }
            finally { Release(buffer); }
            return sample;
        }

        private void ReadOutput()
        {
            OutputDataBuffer output = new OutputDataBuffer();
            output.StreamId = _outputStream;
            bool providesSamples = (_outputFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
            if (!providesSamples)
            {
                IntPtr buffer;
                Check(MFCreateSample(out output.Sample), "MFCreateSample");
                Check(MFCreateMemoryBuffer(_outputSize, out buffer), "MFCreateMemoryBuffer");
                Slot<AddBufferFn>(output.Sample, SampleAddBuffer)(output.Sample, buffer);
                Release(buffer);
            }
            int status;
            int hr = _processOutput(_transform, 0, 1, ref output, out status);
            try
            {
                Check(hr, "ProcessOutput");
                if (output.Sample == IntPtr.Zero)
                    return;
                IntPtr contiguous;
                Check(Slot<ConvertToContiguousFn>(output.Sample, SampleConvert)(output.Sample, out contiguous), "ConvertToContiguousBuffer");
                try
                {
                    IntPtr data;
                    int max, current;
                    Check(Slot<LockFn>(contiguous, BufferLock)(contiguous, out data, out max, out current), "Lock");
                    try
                    {
                        if (current > 0)
                        {
                            byte[] bytes = new byte[current];
                            Marshal.Copy(data, bytes, 0, current);
                            _outputs.Enqueue(bytes);
                        }
                    }
                    finally { Slot<NoArgsFn>(contiguous, BufferUnlock)(contiguous); }
                }
                finally { Release(contiguous); }
            }
            finally
            {
                Release(output.Sample);
                Release(output.Events);
            }
        }

        /// <summary>Does this Annex B data contain a NAL unit of the given type?</summary>
        internal static bool HasNal(byte[] data, int nalType)
        {
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 0x1F) == nalType)
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_started)
            {
                try { Slot<ProcessMessageFn>(_transform, XformMessage)(_transform, MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
            }
            bool wasOpen = _activate != IntPtr.Zero;
            try { Release(_codec); } catch { }
            try { Release(_events); } catch { }
            try { Release(_transform); } catch { }
            if (wasOpen)
            {
                try { Slot<NoArgsFn>(_activate, ActivateShutdown)(_activate); } catch { }
                try { Release(_activate); } catch { }
            }
            Marshal.FreeHGlobal(_variant);
            if (wasOpen)
            {
                try { MFShutdown(); } catch { }
            }
        }
    }
}
