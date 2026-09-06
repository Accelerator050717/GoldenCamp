using System;
using System.Threading;

namespace GoldCamp.Demo
{
    public struct CampVoiceEncoded
    {
        public int Stream;
        public int Sequence;
        public byte[] Data;
        public float Rms;
    }

    // This queue is for the capture/main thread and codec worker, never the audio callback.
    // Codec dependency/pin/license are documented in CampVoiceCodec.cs.
    public sealed class CampVoiceEncodeWorker : IDisposable
    {
        public const int MaximumInputBlocks = 5;
        public const int MaximumOutputPackets = 10;
        public const int StopWaitMilliseconds = 500;

        private struct CaptureBlock
        {
            public float[] Input;
            public int Stream, Sequence;
        }

        private readonly object _gate = new object();
        private readonly CaptureBlock[] _input = new CaptureBlock[MaximumInputBlocks];
        private readonly CampVoiceEncoded[] _output = new CampVoiceEncoded[MaximumOutputPackets];
        private readonly float[] _mono = new float[CampVoiceCodec.FrameSamples];
        private readonly byte[] _packet = new byte[CampVoiceCodec.MaximumPacketBytes];
        private readonly Thread _thread;
        private readonly int _sourceRate, _sourceChannels;
        private int _inputRead, _inputWrite, _inputCount;
        private int _outputRead, _outputWrite, _outputCount;
        private int _latestStream, _activeStream, _previousSequence, _partialSequence, _monoCount;
        private bool _haveLatestStream, _haveActiveStream, _processing;
        private bool _stopRequested, _stopped;
        private int _disposeRequested;
        private long _droppedInputs, _droppedOutputs;
        private string _error = "";

        public int InputBlockSamples { get; }
        public bool IsStopped => Volatile.Read(ref _stopped);
        public string Error => Volatile.Read(ref _error);
        // Includes the block currently being encoded, so at most 100 ms of input is retained.
        public int InputBlockCount { get { lock (_gate) return _inputCount + (_processing ? 1 : 0); } }
        public int OutputPacketCount { get { lock (_gate) return _outputCount; } }
        public long DroppedInputBlocks => Interlocked.Read(ref _droppedInputs);
        public long DroppedOutputPackets => Interlocked.Read(ref _droppedOutputs);

        public CampVoiceEncodeWorker(int sourceRate, int channels = 1)
        {
            if (sourceRate < 8000 || sourceRate > 192000 || sourceRate % 50 != 0)
                throw new ArgumentOutOfRangeException(nameof(sourceRate), "Capture rate must provide an integral 20 ms block.");
            if (channels < 1 || channels > 8) throw new ArgumentOutOfRangeException(nameof(channels));
            _sourceRate = sourceRate;
            _sourceChannels = channels;
            InputBlockSamples = sourceRate / 50 * channels;
            _thread = new Thread(Run) { IsBackground = true, Name = "GoldCamp voice encoder" };
            _thread.Start();
        }

        // Accepted arrays are borrowed immutably until processed. The caller must not
        // modify/recycle them after success. Invalid/full/stopped submissions return false.
        public bool TryPush(float[] input, int stream, int sequence)
        {
            if (input == null || input.Length != InputBlockSamples)
            {
                SetError("Capture block must contain exactly 20 ms of complete interleaved PCM.");
                return false;
            }
            lock (_gate)
            {
                if (_stopRequested || _stopped) return false;
                if (!_haveLatestStream || stream != _latestStream)
                {
                    // Retire queued old-generation audio immediately. An in-flight old
                    // block remains worker-owned and is prevented from publishing below.
                    _haveLatestStream = true;
                    _latestStream = stream;
                    ClearQueuesLocked();
                }
                if (_inputCount + (_processing ? 1 : 0) >= MaximumInputBlocks)
                {
                    Interlocked.Increment(ref _droppedInputs);
                    return false;
                }
                _input[_inputWrite] = new CaptureBlock { Input = input, Stream = stream, Sequence = sequence };
                _inputWrite = (_inputWrite + 1) % MaximumInputBlocks;
                _inputCount++;
                Monitor.Pulse(_gate);
                return true;
            }
        }

        // Data is an exact-sized independently owned packet, safe for the main thread
        // to enqueue/send after the codec worker reuses its 400-byte scratch buffer.
        public bool TryPop(out CampVoiceEncoded encoded)
        {
            lock (_gate)
            {
                encoded = default;
                if (_stopRequested || _outputCount == 0) return false;
                encoded = _output[_outputRead];
                _output[_outputRead] = default;
                _outputRead = (_outputRead + 1) % MaximumOutputPackets;
                _outputCount--;
                return true;
            }
        }

        private void Run()
        {
            CampVoiceEncoder encoder = null;
            try
            {
                encoder = new CampVoiceEncoder(_sourceRate, _sourceChannels);
                while (true)
                {
                    CaptureBlock block;
                    lock (_gate)
                    {
                        while (!_stopRequested && _inputCount == 0) Monitor.Wait(_gate);
                        if (_stopRequested) break;
                        block = _input[_inputRead];
                        _input[_inputRead] = default;
                        _inputRead = (_inputRead + 1) % MaximumInputBlocks;
                        _inputCount--;
                        _processing = true;
                    }
                    Process(encoder, block);
                    lock (_gate) _processing = false;
                }
            }
            catch (Exception exception)
            {
                SetError(exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                // The worker alone disposes its codec. Dispose() on the main thread
                // may time out, but it never races this operation or clears live scratch.
                try { encoder?.Dispose(); }
                catch (Exception exception) { SetError(exception.GetType().Name + ": " + exception.Message); }
                Array.Clear(_mono, 0, _mono.Length);
                Array.Clear(_packet, 0, _packet.Length);
                lock (_gate)
                {
                    _stopRequested = true;
                    _processing = false;
                    ClearQueuesLocked();
                    Volatile.Write(ref _stopped, true);
                }
            }
        }

        private void Process(CampVoiceEncoder encoder, CaptureBlock block)
        {
            if (!IsCurrent(block.Stream)) return;
            bool discontinuity = !_haveActiveStream || block.Stream != _activeStream ||
                block.Sequence != unchecked(_previousSequence + 1);
            if (discontinuity)
            {
                encoder.Reset();
                _monoCount = 0;
                Array.Clear(_mono, 0, _mono.Length);
            }
            _haveActiveStream = true;
            _activeStream = block.Stream;
            _previousSequence = block.Sequence;
            int offset = 0;
            while (offset < block.Input.Length)
            {
                if (!IsCurrent(block.Stream)) return;
                if (_monoCount == 0) _partialSequence = block.Sequence;
                int produced = encoder.Resample(block.Input.AsSpan(offset), _mono.AsSpan(_monoCount), out int consumedFrames);
                int consumed = consumedFrames * _sourceChannels;
                if (consumed < 0 || consumed > block.Input.Length - offset || produced < 0 ||
                    produced > _mono.Length - _monoCount || (consumed == 0 && produced == 0))
                    throw new InvalidOperationException("Voice resampling made no valid forward progress.");
                offset += consumed;
                _monoCount += produced;
                if (_monoCount != CampVoiceCodec.FrameSamples) continue;
                double squareSum = 0d;
                for (int i = 0; i < _mono.Length; i++) squareSum += (double)_mono[i] * _mono[i];
                float rms = (float)Math.Sqrt(squareSum / CampVoiceCodec.FrameSamples);
                int count = encoder.EncodeFrame(_mono, _packet);
                Publish(block.Stream, _partialSequence, count, rms);
                _monoCount = 0;
            }
            // A short real resampler result stays in _mono for the next contiguous block.
            // No zero padding or altered sample clock is used to manufacture an output frame.
        }

        private void Publish(int stream, int sequence, int count, float rms)
        {
            lock (_gate)
            {
                if (_stopRequested || stream != _latestStream) return;
                if (_outputCount >= MaximumOutputPackets)
                {
                    Interlocked.Increment(ref _droppedOutputs);
                    return;
                }
                var data = new byte[count];
                Buffer.BlockCopy(_packet, 0, data, 0, count);
                _output[_outputWrite] = new CampVoiceEncoded { Stream = stream, Sequence = sequence, Data = data, Rms = rms };
                _outputWrite = (_outputWrite + 1) % MaximumOutputPackets;
                _outputCount++;
            }
        }

        private bool IsCurrent(int stream)
        {
            lock (_gate) return !_stopRequested && _haveLatestStream && stream == _latestStream;
        }

        private void ClearQueuesLocked()
        {
            Array.Clear(_input, 0, _input.Length);
            Array.Clear(_output, 0, _output.Length);
            _inputRead = _inputWrite = _inputCount = 0;
            _outputRead = _outputWrite = _outputCount = 0;
        }

        private void SetError(string message)
        {
            if (message == null) message = "Voice encoder failed.";
            Volatile.Write(ref _error, message.Length > 256 ? message.Substring(0, 256) : message);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
            lock (_gate)
            {
                _stopRequested = true;
                ClearQueuesLocked();
                Monitor.PulseAll(_gate);
            }
            if (Thread.CurrentThread != _thread && !_thread.Join(StopWaitMilliseconds))
                SetError("Voice encoder is still finishing; resources remain owned by its background thread.");
        }
    }
}
