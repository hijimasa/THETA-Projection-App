using System;
using System.IO;
using System.Text;

namespace ThetaProjection
{
    /// <summary>
    /// multipart/x-mixed-replace (MotionJPEG) ストリームから JPEG フレームを 1 枚ずつ
    /// 取り出すリーダー。THETA はパートごとに Content-Length ヘッダを付けるので
    /// それを優先し、無い場合は JPEG の SOI/EOI マーカー走査にフォールバックする。
    /// ネットワークストリームを直接渡すと 1 バイト読みが遅いので、呼び出し側で
    /// BufferedStream にくるむことを推奨。
    /// </summary>
    public sealed class MjpegStreamReader
    {
        private readonly Stream _stream;
        private readonly StringBuilder _lineBuffer = new StringBuilder(128);

        public MjpegStreamReader(Stream stream)
        {
            _stream = stream;
        }

        /// <summary>
        /// 次の JPEG フレームを読み出す。ストリームが終了したら null。
        /// </summary>
        public byte[] ReadFrame()
        {
            int contentLength = -1;
            bool sawAnyHeader = false;

            // パート境界とヘッダ行を読み進める
            while (true)
            {
                string line = ReadLine();
                if (line == null)
                    return null; // ストリーム終了

                if (line.Length == 0)
                {
                    // 空行 = ヘッダ終了。ヘッダを一つも見ていない空行(境界前後の揺れ)は読み飛ばす
                    if (sawAnyHeader || contentLength > 0)
                        break;
                    continue;
                }

                sawAnyHeader = true;
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    string name = line.Substring(0, colon).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line.Substring(colon + 1).Trim(), out contentLength);
                    }
                }
            }

            if (contentLength > 0)
                return ReadExactly(contentLength);

            // Content-Length が無い場合: SOI(FFD8)〜EOI(FFD9) を走査
            return ScanJpeg();
        }

        /// <summary>CRLF または LF 終端の 1 行を ASCII で読む。EOF なら null。</summary>
        private string ReadLine()
        {
            _lineBuffer.Length = 0;
            while (true)
            {
                int b = _stream.ReadByte();
                if (b < 0)
                    return _lineBuffer.Length > 0 ? _lineBuffer.ToString() : null;
                if (b == '\n')
                    return _lineBuffer.ToString();
                if (b != '\r')
                    _lineBuffer.Append((char)b);

                // ヘッダ行としては異常に長い → バイナリに突入している。行として扱わず打ち切る
                if (_lineBuffer.Length > 512)
                    return _lineBuffer.ToString();
            }
        }

        private byte[] ReadExactly(int count)
        {
            var buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = _stream.Read(buf, offset, count - offset);
                if (n <= 0)
                    return null;
                offset += n;
            }
            return buf;
        }

        private byte[] ScanJpeg()
        {
            // SOI (0xFF 0xD8) を探す
            int prev = -1;
            while (true)
            {
                int b = _stream.ReadByte();
                if (b < 0) return null;
                if (prev == 0xFF && b == 0xD8) break;
                prev = b;
            }

            using (var ms = new MemoryStream(64 * 1024))
            {
                ms.WriteByte(0xFF);
                ms.WriteByte(0xD8);
                prev = -1;
                while (true)
                {
                    int b = _stream.ReadByte();
                    if (b < 0) return null;
                    ms.WriteByte((byte)b);
                    if (prev == 0xFF && b == 0xD9) // EOI
                        return ms.ToArray();
                    prev = b;
                }
            }
        }
    }
}
