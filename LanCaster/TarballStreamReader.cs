using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace WellDunne.LanCaster
{
    /// <summary>
    /// A tarball entry describing a relative path and length of a file.
    /// </summary>
    public class TarballEntry
    {
        public readonly string RelativePath;
        public readonly long Length;
        public readonly byte[] SHA256Hash;

        public TarballEntry(string relPath, long length, byte[] hash)
        {
            this.RelativePath = relPath;
            this.Length = length;
            this.SHA256Hash = hash;
        }
    }

    /// <summary>
    /// A stream that writes out individual files concatenated together from a TarballStreamWriter.
    /// </summary>
    public class TarballStreamReader : Stream
    {
        #region Private state

        private DirectoryInfo _baseDir;
        private List<TarballEntry> _files;
        private List<long> _positions;
        private long _totalLength;
        private long _currPos;
        private int _currIndex;
        private FileStream _currFile = null;
        private long _currSize = 0;

        #endregion

        /// <summary>
        /// Constructs a tarball stream reader which splits up a concatenated stream into individual
        /// files.
        /// </summary>
        /// <param name="baseDir">The base directory to create the files in</param>
        /// <param name="files">The list of tarball entries in the stream</param>
        public TarballStreamReader(DirectoryInfo baseDir, IEnumerable<TarballEntry> files)
        {
            this._baseDir = baseDir;
            this._files = files.ToList();

            this._currPos = 0;
            this._currIndex = 0;
            this._totalLength = 0;
            this._positions = new List<long>(this._files.Count);
            foreach (var fi in this._files)
            {
                this._positions.Add(this._totalLength);
                this._totalLength += fi.Length;
            }
        }

        [Conditional("TarballTracing")]
        private static void trace(string format, params object[] args)
        {
            Trace.WriteLine(String.Format(format, args), "reader");
        }

        public DirectoryInfo BaseDirectory { get { return _baseDir; } }
        public List<TarballEntry> Files { get { return _files; } }


        #region Public events

        public delegate void NotifyFileOpenedDelegate(TarballEntry file, bool created);
        public delegate void NotifyFileClosedDelegate(TarballEntry file);
        public delegate void NotifyFileWrittenDelegate(TarballEntry file, long pos, int count);

        public event NotifyFileOpenedDelegate NotifyFileOpened;
        public event NotifyFileClosedDelegate NotifyFileClosed;
        public event NotifyFileWrittenDelegate NotifyFileWritten;

        #endregion

        #region Stream implementation

        public override void Close()
        {
            if (this._currFile == null) return;
            if (this._currIndex >= this._files.Count) this._currIndex = this._files.Count - 1;
            // Move on to the next file:
            trace("Closing file '{0}'", this._files[this._currIndex].RelativePath);
            this._currFile.Flush();
            this._currFile.Close();
            if (NotifyFileClosed != null) NotifyFileClosed(this._files[this._currIndex]);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (this._currFile != null)
            {
                if (this._currIndex >= this._files.Count) this._currIndex = this._files.Count - 1;
                trace("Closing file '{0}'", this._files[this._currIndex].RelativePath);
                this._currFile.Flush();
                this._currFile.Close();
                if (NotifyFileClosed != null) NotifyFileClosed(this._files[this._currIndex]);
            }
        }

        public override bool CanRead
        {
            get { return false; }
        }

        public override bool CanSeek
        {
            get { return false; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override void Flush()
        {
            if (this._currIndex >= this._files.Count)
            {
                if (this._currFile != null)
                {
                    trace("Closing file '{0}'", this._files[this._files.Count - 1].RelativePath);
                    this._currFile.Close();
                    this._currFile = null;
                    if (NotifyFileClosed != null) NotifyFileClosed(this._files[this._files.Count - 1]);
                }
                return;
            }
            if (this._currFile != null)
            {
                this._currFile.Flush();
            }
        }

        public override long Length
        {
            get { return this._totalLength; }
        }

        public override long Position
        {
            get
            {
                return this._currPos;
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin != SeekOrigin.Begin) throw new NotSupportedException("Only SeekOrigin.Begin is supported");
            if (offset < 0) throw new ArgumentOutOfRangeException("offset must be >= 0");
            // No work to do?
            if (offset == this._currPos) return offset;

            // Discover the file we should be at:
            int idx = this._positions.BinarySearch(offset);
            if (idx < 0)
            {
                idx = ~idx - 1;
            }
            if (idx >= this._files.Count)
            {
                // Seeked too far?
                return -1;
            }

            this._currIndex = idx;
            this._currPos = offset;
            if (this._currFile != null)
            {
                trace("Closing file '{0}'", this._files[this._files.Count - 1].RelativePath);
                this._currFile.Close();
                this._currFile = null;
                if (NotifyFileClosed != null) NotifyFileClosed(this._files[this._files.Count - 1]);
            }

            return offset;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // No more space for writing:
            if (this._currIndex >= this._files.Count)
            {
                if (this._currFile != null)
                {
                    trace("Closing file '{0}'", this._files[this._files.Count - 1].RelativePath);
                    this._currFile.Close();
                    this._currFile = null;
                    if (NotifyFileClosed != null) NotifyFileClosed(this._files[this._files.Count - 1]);
                }
                return;
            }

            int totWritten = 0;
            while (totWritten < count)
            {
                // Open the next file if necessary:
                TarballEntry entry;
                if (this._currFile == null)
                {
                    entry = this._files[this._currIndex];
                    this._currSize = entry.Length;

                    string relPath = entry.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);
                    if ((relPath.Length >= 1) && relPath.StartsWith(System.IO.Path.DirectorySeparatorChar.ToString())) relPath = relPath.Substring(1);

                    // Create the directory structure:
                    int lastSlash = relPath.LastIndexOf(System.IO.Path.DirectorySeparatorChar);
                    if (lastSlash >= 0)
                    {
                        string dir = relPath.Substring(0, lastSlash);
                        string absDirPath = Path.Combine(this._baseDir.FullName, dir);
                        if (!Directory.Exists(absDirPath)) Directory.CreateDirectory(absDirPath);
                    }

                    string absPath = Path.Combine(this._baseDir.FullName, relPath);

                    // Create the file and allocate its length:
                    var file = new FileInfo(absPath);
                    if (file.Exists)
                    {
                        this._currFile = file.Open(FileMode.Open, FileAccess.Write, FileShare.Write);
                        this._currFile.Seek(0, SeekOrigin.Begin);
                        if (NotifyFileOpened != null) NotifyFileOpened(entry, false);
                    }
                    else
                    {
                        this._currFile = file.Open(FileMode.CreateNew, FileAccess.Write, FileShare.Write);
                        this._currFile.SetLength(this._currSize);
                        if (NotifyFileOpened != null) NotifyFileOpened(entry, true);
                    }
                }
                else
                {
                    entry = this._files[this._currIndex];
                }

                bool closeFile = false;

                // Seek to the correct position to write at:
                this._currFile.Seek(this._currPos - this._positions[this._currIndex], SeekOrigin.Begin);

                // Write out to the file:
                if (this._currFile.Position + (count - totWritten) <= this._currSize)
                {
                    int diff = count - totWritten;
                    trace("Writing {1} bytes to file '{0}'", entry.RelativePath, diff);
                    long pos = this._currFile.Position;
                    
                    this._currFile.Write(buffer, offset + totWritten, diff);

                    trace("Writing {1} bytes to file '{0}' complete", entry.RelativePath, diff);
                    
                    totWritten += diff;
                    this._currPos += diff;
                    Debug.Assert(this._currPos == this._currFile.Position + this._positions[this._currIndex]);
                    if (NotifyFileWritten != null) NotifyFileWritten(entry, pos, diff);

                    // Determine if we want to close the file:
                    closeFile = (this._currFile.Position >= this._currSize);
                }
                else
                {
                    // Finish off the file:
                    int diff = Math.Min((count - totWritten), (int)(this._currSize - this._currFile.Position));
                    if (diff > 0)
                    {
                        trace("Writing {1} bytes to file '{0}'", entry.RelativePath, diff);
                        long pos = this._currFile.Position;

                        this._currFile.Write(buffer, offset + totWritten, diff);

                        trace("Writing {1} bytes to file '{0}' complete", entry.RelativePath, diff);
                        
                        totWritten += diff;
                        this._currPos += diff;
                        Debug.Assert(this._currPos == this._positions[this._currIndex] + this._currFile.Position);
                        Debug.Assert(this._currPos == this._positions[this._currIndex] + this._currSize);
                        if (NotifyFileWritten != null) NotifyFileWritten(entry, pos, diff);
                    }

                    closeFile = true;
                }

                // Close the current file and move on:
                if (closeFile)
                {
                    // Move on to the next file:
                    trace("Closing file '{0}'", entry.RelativePath);
                    this._currFile.Close();
                    this._currFile = null;
                    if (NotifyFileClosed != null) NotifyFileClosed(entry);

                    ++this._currIndex;
                    if (this._currIndex >= this._files.Count)
                    {
                        // Done with all files:
                        break;
                    }
                }
            }
        }

        #endregion
    }
}
