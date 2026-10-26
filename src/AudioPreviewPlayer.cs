using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace DownloadSearchAudio
{
    internal sealed class AudioPreviewPlayer : IDisposable
    {
        private sealed class Session
        {
            public string Path;
            public AudioFileReader Reader;
            public WaveOut Output;
            public readonly TaskCompletionSource<bool> Stopped =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Cleaned;
        }

        private readonly object _sync = new object();
        private readonly SemaphoreSlim _transition = new SemaphoreSlim(1, 1);
        private Session _session;
        private int _requestId;
        private bool _disposed;

        public event Action<string, Exception> PlaybackFailed;
        public event Action<string, bool> PlaybackChanged;

        public async Task PlayAsync(string path)
        {
            int requestId = Interlocked.Increment(ref _requestId);
            await _transition.WaitAsync();
            try
            {
                if (!IsCurrent(requestId)) return;

                Session previous;
                lock (_sync) previous = _session;
                if (previous != null && string.Equals(previous.Path, path, StringComparison.OrdinalIgnoreCase)
                    && previous.Output.PlaybackState == PlaybackState.Playing)
                    return;

                if (previous != null)
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_session, previous)) _session = null;
                    }
                    await StopSessionAsync(previous);
                }
                if (!IsCurrent(requestId)) return;

                AudioFileReader reader = await Task.Run(delegate { return new AudioFileReader(path); });
                if (!IsCurrent(requestId))
                {
                    reader.Dispose();
                    return;
                }

                WaveOut output = null;
                Session session = null;

                try
                {
                    output = new WaveOut();
                    session = new Session { Path = path, Reader = reader, Output = output };
                    output.PlaybackStopped += delegate(object sender, StoppedEventArgs args)
                    {
                        OnPlaybackStopped(session, args);
                    };
                    output.Init(reader);
                    lock (_sync)
                    {
                        if (_disposed || requestId != _requestId)
                        {
                            Cleanup(session);
                            return;
                        }
                        _session = session;
                    }
                    output.Play();
                    PlaybackChanged?.Invoke(path, true);
                }
                catch
                {
                    if (session != null)
                    {
                        lock (_sync)
                        {
                            if (ReferenceEquals(_session, session)) _session = null;
                        }
                        Cleanup(session);
                    }
                    else
                    {
                        if (output != null) try { output.Dispose(); } catch { }
                        reader.Dispose();
                    }
                    throw;
                }
            }
            catch (Exception ex)
            {
                if (IsCurrent(requestId)) PlaybackFailed?.Invoke(path, ex);
            }
            finally
            {
                _transition.Release();
            }
        }

        public async Task StopAsync()
        {
            Interlocked.Increment(ref _requestId);
            await _transition.WaitAsync();
            try
            {
                Session current;
                lock (_sync)
                {
                    current = _session;
                    _session = null;
                }
                if (current != null) await StopSessionAsync(current);
            }
            finally
            {
                _transition.Release();
            }
        }

        private bool IsCurrent(int requestId)
        {
            lock (_sync) return !_disposed && requestId == _requestId;
        }

        private void OnPlaybackStopped(Session session, StoppedEventArgs args)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_session, session)) _session = null;
            }
            Cleanup(session);
            PlaybackChanged?.Invoke(session.Path, false);
            if (args.Exception != null)
            {
                bool report;
                lock (_sync) report = !_disposed;
                if (report) PlaybackFailed?.Invoke(session.Path, args.Exception);
            }
        }

        private static async Task StopSessionAsync(Session session)
        {
            try { session.Output.Stop(); }
            catch { Cleanup(session); }

            Task completed = await Task.WhenAny(session.Stopped.Task, Task.Delay(2000));
            if (!ReferenceEquals(completed, session.Stopped.Task)) Cleanup(session);
        }

        private static void Cleanup(Session session)
        {
            if (Interlocked.Exchange(ref session.Cleaned, 1) != 0) return;
            try { session.Output.Dispose(); } catch { }
            try { session.Reader.Dispose(); } catch { }
            session.Stopped.TrySetResult(true);
        }

        public void Dispose()
        {
            Session current;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                Interlocked.Increment(ref _requestId);
                current = _session;
                _session = null;
            }

            if (current != null)
                _ = StopSessionAsync(current);
        }
    }
}
