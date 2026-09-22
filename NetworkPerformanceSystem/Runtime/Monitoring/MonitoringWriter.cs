using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace NetworkPerformanceSystem.Runtime {

    /// <summary>
    /// Puts monitoring records on disk without the main thread ever waiting on it.
    ///
    /// Records are queued from the main thread and written by one background thread, which also
    /// rotates the file (hourly, or at 64MB) and compresses each file as it is closed. The queue
    /// is bounded, so a disk that stops accepting writes costs dropped records rather than
    /// memory; and the total on disk is capped, so a module somebody forgot to switch off cannot
    /// fill a server's drive. When the cap is reached collection stops - nothing already written
    /// is ever deleted to make room.
    ///
    /// Nothing here logs. BepInEx's log listeners are not something to call from a second thread
    /// for the sake of a diagnostic, so faults are left in a field for the main thread to report.
    /// </summary>
    internal sealed class MonitoringWriter {

        private const long RotateBytes = 64L * 1024L * 1024L;
        private const double RotateSeconds = 3600d;
        private const int MaxQueuedLines = 200000;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly string _directory;
        private readonly long _maxStoredBytes;

        private Thread _thread;
        private volatile bool _stopping;
        private int _queued;

        private StreamWriter _current;
        private string _currentPath;
        private long _currentBytes;
        private DateTime _currentOpenedUtc;
        private int _fileIndex;

        /// <summary>Bytes in files that are closed: whatever the folder held before this session
        /// plus every file this session has finished with, at its compressed size.</summary>
        private long _closedBytes;

        private long _recordsWritten;
        private long _recordsDropped;

        internal long RecordsWritten => Interlocked.Read(ref _recordsWritten);
        internal long RecordsDropped => Interlocked.Read(ref _recordsDropped);
        internal long StoredBytes => Interlocked.Read(ref _closedBytes) + Interlocked.Read(ref _currentBytes);
        internal string Directory => _directory;

        /// <summary>The cap was reached. Latched: the session writes nothing further.</summary>
        internal volatile bool DiskFull;

        /// <summary>Set once if the writer thread died, for the main thread to report.</summary>
        internal volatile string Fault;

        internal MonitoringWriter(string directory, long maxStoredBytes, long alreadyStoredBytes) {
            _directory = directory;
            _maxStoredBytes = maxStoredBytes;
            _closedBytes = alreadyStoredBytes;
        }

        internal void Start() {
            System.IO.Directory.CreateDirectory(_directory);
            _thread = new Thread(Run) { IsBackground = true, Name = "NPS monitoring writer" };
            _thread.Start();
        }

        internal void Enqueue(string line) {
            if (line == null || _stopping || DiskFull || Fault != null) { return; }

            if (Volatile.Read(ref _queued) >= MaxQueuedLines) {
                Interlocked.Increment(ref _recordsDropped);
                return;
            }

            Interlocked.Increment(ref _queued);
            _queue.Enqueue(line);
        }

        /// <summary>Flush, close and compress. Waits briefly for the thread; a process that is
        /// exiting anyway will not wait longer, and the raw file is complete either way because
        /// compression only removes it after the compressed copy has been renamed into place.</summary>
        internal void Stop() {
            _stopping = true;
            _wake.Set();
            if (_thread != null && _thread.IsAlive) { _thread.Join(5000); }
        }

        private void Run() {
            try {
                while (true) {
                    _wake.WaitOne(1000);
                    Drain();
                    if (_stopping) {
                        Drain();
                        break;
                    }
                }
            } catch (Exception e) {
                Fault = e.GetType().Name + ": " + e.Message;
            } finally {
                try { CloseCurrent(); } catch (Exception) { }
            }
        }

        private void Drain() {
            bool wrote = false;
            while (_queue.TryDequeue(out string line)) {
                Interlocked.Decrement(ref _queued);

                if (DiskFull) {
                    Interlocked.Increment(ref _recordsDropped);
                    continue;
                }

                if (_current == null) { OpenNext(); }

                _current.Write(line);
                _current.Write('\n');
                // Records are ASCII by construction, so characters are bytes.
                Interlocked.Add(ref _currentBytes, line.Length + 1);
                Interlocked.Increment(ref _recordsWritten);
                wrote = true;

                if (StoredBytes >= _maxStoredBytes) {
                    DiskFull = true;
                    CloseCurrent();
                    wrote = false;
                    continue;
                }

                if (Interlocked.Read(ref _currentBytes) >= RotateBytes
                    || (DateTime.UtcNow - _currentOpenedUtc).TotalSeconds >= RotateSeconds) {
                    CloseCurrent();
                    wrote = false;
                }
            }

            if (wrote) { _current?.Flush(); }
        }

        private void OpenNext() {
            _fileIndex++;
            _currentPath = Path.Combine(_directory, "events-" + _fileIndex.ToString("0000") + ".jsonl");
            _current = new StreamWriter(new FileStream(_currentPath, FileMode.Create, FileAccess.Write, FileShare.Read), Utf8NoBom);
            Interlocked.Exchange(ref _currentBytes, 0L);
            _currentOpenedUtc = DateTime.UtcNow;
        }

        private void CloseCurrent() {
            if (_current == null) { return; }

            _current.Flush();
            _current.Dispose();
            _current = null;

            long rawBytes = Interlocked.Exchange(ref _currentBytes, 0L);
            Interlocked.Add(ref _closedBytes, Compress(_currentPath, rawBytes));
            _currentPath = null;
        }

        /// <summary>
        /// Compress a finished file and return what it now costs on disk. The compressed copy is
        /// written beside the original and renamed into place before the original goes, so being
        /// killed part-way leaves the complete raw file and a stray .tmp, never a half file
        /// standing in for a whole one. Any failure keeps the raw file and says so by size.
        /// </summary>
        private static long Compress(string path, long rawBytes) {
            string temp = path + ".gz.tmp";
            string final = path + ".gz";
            try {
                using (FileStream input = File.OpenRead(path))
                using (FileStream output = File.Create(temp))
                using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress)) {
                    input.CopyTo(gzip);
                }
                File.Move(temp, final);
                File.Delete(path);
                return new FileInfo(final).Length;
            } catch (Exception) {
                return rawBytes;
            }
        }

        /// <summary>Everything already under a folder, for the cap. Zero if it does not exist.</summary>
        internal static long MeasureDirectory(string directory) {
            try {
                if (!System.IO.Directory.Exists(directory)) { return 0L; }

                long total = 0L;
                foreach (string file in System.IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) {
                    total += new FileInfo(file).Length;
                }
                return total;
            } catch (Exception) {
                return 0L;
            }
        }
    }
}
