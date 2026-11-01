using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Maela
{
    /// <summary>
    /// Microphone capture runs on its own thread, never inside the Windows audio callback.
    ///
    /// WinMM calls the callback on a driver thread while holding its own internal lock. If that
    /// callback then waits for one of our locks while another thread holds that lock and opens a
    /// speaker (waveOutOpen, which needs the WinMM lock), both wait forever and mæla freezes. It
    /// struck when someone new started talking. So the callback only notes which buffer is ready;
    /// this thread does the echo cancelling, filtering, encoding and sending, then hands the
    /// buffer back to the microphone.
    /// </summary>
    internal sealed partial class VoipEngine
    {
        private struct CapturedBuffer
        {
            public IntPtr Header;
            public long CaptureEnd;
        }

        private readonly object _captureLock = new object();
        private readonly Queue<CapturedBuffer> _captureQueue = new Queue<CapturedBuffer>();
        private Thread _captureThread;
        private volatile bool _captureStopping;

        private void StartCaptureThread()
        {
            _captureStopping = false;
            _captureThread = new Thread(CaptureLoop);
            _captureThread.IsBackground = true;
            _captureThread.Name = "Maela microphone";
            _captureThread.Priority = ThreadPriority.AboveNormal;
            _captureThread.Start();
        }

        /// <summary>WinMM callback: record which buffer is ready and wake the capture thread. Nothing else.</summary>
        private void WaveInCallback(IntPtr hwi, uint message, IntPtr instance, IntPtr headerPtr, IntPtr reserved)
        {
            if (message != NativeMethods.WIM_DATA || headerPtr == IntPtr.Zero)
                return;
            CapturedBuffer item;
            item.Header = headerPtr;
            // The capture clock must advance for every frame, muted or not, so the echo
            // canceller's view of the speaker timeline stays aligned with the microphone.
            int recorded = Marshal.ReadInt32(headerPtr, IntPtr.Size + 4); // WAVEHDR.dwBytesRecorded
            item.CaptureEnd = recorded > 0 ? _farEnd.AdvanceCapture(recorded / 2) : _farEnd.CaptureClock;
            lock (_captureQueue)
            {
                _captureQueue.Enqueue(item);
                Monitor.Pulse(_captureQueue);
            }
        }

        private void CaptureLoop()
        {
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            while (true)
            {
                CapturedBuffer item;
                lock (_captureQueue)
                {
                    while (_captureQueue.Count == 0 && !_captureStopping)
                        Monitor.Wait(_captureQueue, 200);
                    if (_captureQueue.Count == 0)
                        return; // Stopping and drained.
                    item = _captureQueue.Dequeue();
                }

                try
                {
                    WAVEHDR header = (WAVEHDR)Marshal.PtrToStructure(item.Header, typeof(WAVEHDR));
                    if (!_captureStopping && _running && MicrophoneOpen && header.dwBytesRecorded == AudioBytesPerFrame)
                    {
                        byte[] pcm = new byte[AudioBytesPerFrame];
                        Marshal.Copy(header.lpData, pcm, 0, AudioBytesPerFrame);
                        ProcessCapturedFrame(pcm, item.CaptureEnd);
                    }

                    // Hand the buffer back, unless the microphone is being closed.
                    lock (_captureLock)
                    {
                        if (!_captureStopping && _running && _waveIn != IntPtr.Zero)
                        {
                            header.dwBytesRecorded = 0;
                            Marshal.StructureToPtr(header, item.Header, false);
                            NativeMethods.waveInAddBuffer(_waveIn, item.Header, (uint)headerSize);
                        }
                    }
                }
                catch
                {
                    // Never let one bad frame take the microphone down.
                }
            }
        }

        /// <summary>Stops handing buffers back and waits for the capture thread to finish with them.</summary>
        private IntPtr StopCapture()
        {
            IntPtr waveIn;
            lock (_captureLock)
            {
                _captureStopping = true;
                waveIn = _waveIn;
                _waveIn = IntPtr.Zero;
            }
            lock (_captureQueue)
                Monitor.PulseAll(_captureQueue);
            Thread thread = _captureThread;
            _captureThread = null;
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                try { thread.Join(1000); } catch { }
            }
            lock (_captureQueue)
                _captureQueue.Clear();
            return waveIn;
        }
    }
}
