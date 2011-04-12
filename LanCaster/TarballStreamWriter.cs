using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace WellDunne.LanCaster
{
    /// <summary>
    /// A stream that constructs a tarball on-the-fly from a list of FileInfo entries.
    /// Specific file information is not included in the tarball. All the files are simply
    /// concatenated into one virtual stream.
    /// </summary>
    public class TarballStreamWriter : Stream
    {
        private List<FileInfo> _files;
        private List<long> _positions;
        private long _totalLength;
        private long _currPos;
        private FileStream _currFile = null;
        private int _currIndex = 0;

        /// <summary>
        /// Constructs a tarball stream writer which concatenates all files together into
        /// one virtual stream.
        /// </summary>
        /// <param name="files"></param>
        public TarballStreamWriter(IEnumerable<FileInfo> files)
        {
            this._files = files.ToList();

            this._currPos = 0;
            this._currIndex = 0;
            this._currFile = null;

            this._totalLength = 0;
            this._positions = new List<long>(this._files.Count);
            foreach (var fi in this._files)
            {
                this._positions.Add(this._totalLength);
                this._totalLength += fi.Length;
            }
        }

        public System.Collections.ObjectModel.ReadOnlyCollection<FileInfo> Files
        {
            get { return new System.Collections.ObjectModel.ReadOnlyCollection<FileInfo>(_files); }
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return true; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override void Flush()
        {
            throw new NotImplementedException();
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
            // No more data to read?
            if (this._currIndex >= this._files.Count)
            {
                return 0;
            }

            // Read more data from the current file:
            int totRead = 0;
            while (totRead < count)
            {
                if (this._currFile == null)
                {
                    this._currFile = this._files[this._currIndex].OpenRead();
                }

                this._currFile.Seek(this._currPos - this._positions[this._currIndex], SeekOrigin.Begin);

                int nr = this._currFile.Read(buffer, offset + totRead, count - totRead);
                totRead += nr;
                this._currPos += nr;
                Debug.Assert(this._currPos == this._currFile.Position + this._positions[this._currIndex]);
                if (nr < (count - totRead))
                {
                    // Hit the end of this file:
                    ++this._currIndex;
                    this._currFile.Close();
                    this._currFile = null;
                    // Are we out of files?
                    if (this._currIndex >= this._files.Count)
                    {
                        this._currIndex = this._files.Count;
                        break;
                    }
                }
            }

            return totRead;
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
                this._currFile.Close();
                this._currFile = null;
            }

            return 0;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }
}
