using System;
using System.Collections.Generic;

namespace AlUmmahNow.App.Navigation
{
    public enum DpadKey { Up, Down, Left, Right }

    /// <summary>
    /// Remote-control focus over rails of cards. Rows remember their column, and in RTL
    /// the Left key moves forward because the row is mirrored.
    /// </summary>
    public sealed class TvFocusGrid
    {
        private readonly IReadOnlyList<int> _rowLengths;
        private readonly int[] _columns;

        public TvFocusGrid(IReadOnlyList<int> rowLengths)
        {
            _rowLengths = rowLengths;
            _columns = new int[rowLengths.Count];
        }

        public int Row { get; private set; }
        public int Column => _rowLengths.Count == 0 ? 0 : _columns[Row];

        /// <summary>Moves focus and returns whether it changed.</summary>
        public bool Move(DpadKey key, bool isRtl)
        {
            if (_rowLengths.Count == 0) return false;
            switch (key)
            {
                case DpadKey.Up:
                    if (Row == 0) return false;
                    Row--;
                    return true;
                case DpadKey.Down:
                    if (Row == _rowLengths.Count - 1) return false;
                    Row++;
                    return true;
                default:
                    var forward = (key == DpadKey.Right) != isRtl;
                    var next = _columns[Row] + (forward ? 1 : -1);
                    if (next < 0 || next >= _rowLengths[Row]) return false;
                    _columns[Row] = next;
                    return true;
            }
        }
    }
}
