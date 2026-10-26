using System;
using System.Security.Cryptography;

namespace RaidZero.Shared.Ids
{
    /// <summary>
    /// Generates UUIDv7 identifiers (RFC 9562) as lowercase canonical strings.
    ///
    /// Layout: 48-bit Unix milliseconds | version 7 | 12-bit sequence | variant | 62 random bits.
    /// IDs from one generator are strictly increasing, even within the same millisecond, which keeps
    /// database index inserts append-only and makes IDs sortable by creation time.
    ///
    /// Both the backend and the Unity game server mint item IDs with this, so every ID in the
    /// economy has the same shape no matter who created it.
    /// </summary>
    public sealed class Uuid7
    {
        private const int MaxSequence = 0xFFF;

        private static readonly Uuid7 DefaultGenerator = new Uuid7();

        private readonly Func<long> _nowUnixMs;
        private readonly RandomNumberGenerator _random;
        private readonly object _lock = new object();
        private readonly byte[] _randomBuffer = new byte[10];

        private long _lastMs = -1;
        private int _sequence;

        public Uuid7() : this(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), RandomNumberGenerator.Create())
        {
        }

        /// <summary>Creates a generator with an injectable clock, for tests.</summary>
        public Uuid7(Func<long> nowUnixMs, RandomNumberGenerator random)
        {
            _nowUnixMs = nowUnixMs ?? throw new ArgumentNullException(nameof(nowUnixMs));
            _random = random ?? throw new ArgumentNullException(nameof(random));
        }

        /// <summary>A new UUIDv7 from the process-wide generator.</summary>
        public static string NewId()
        {
            return DefaultGenerator.Next();
        }

        /// <summary>A new UUIDv7 from this generator, strictly greater than the previous one.</summary>
        public string Next()
        {
            var bytes = new byte[16];
            lock (_lock)
            {
                long ms = _nowUnixMs();
                _random.GetBytes(_randomBuffer);

                if (ms > _lastMs)
                {
                    _lastMs = ms;
                    // Start the sequence at a random point in its lower half, leaving room to count up.
                    _sequence = ((_randomBuffer[0] << 8) | _randomBuffer[1]) & 0x7FF;
                }
                else
                {
                    // Same millisecond, or the clock went backwards: keep counting from the last ID.
                    _sequence++;
                    if (_sequence > MaxSequence)
                    {
                        _lastMs++;
                        _sequence = 0;
                    }
                }

                long timestamp = _lastMs;
                bytes[0] = (byte)(timestamp >> 40);
                bytes[1] = (byte)(timestamp >> 32);
                bytes[2] = (byte)(timestamp >> 24);
                bytes[3] = (byte)(timestamp >> 16);
                bytes[4] = (byte)(timestamp >> 8);
                bytes[5] = (byte)timestamp;
                bytes[6] = (byte)(0x70 | ((_sequence >> 8) & 0x0F));
                bytes[7] = (byte)_sequence;
                bytes[8] = (byte)(0x80 | (_randomBuffer[2] & 0x3F));
                Array.Copy(_randomBuffer, 3, bytes, 9, 7);
            }

            return Format(bytes);
        }

        /// <summary>True when the string is a canonical lowercase UUID of any version.</summary>
        public static bool IsValid(string value)
        {
            if (value == null || value.Length != 36)
            {
                return false;
            }

            for (int i = 0; i < 36; i++)
            {
                char c = value[i];
                if (i == 8 || i == 13 || i == 18 || i == 23)
                {
                    if (c != '-')
                    {
                        return false;
                    }
                }
                else if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Reads the creation time out of a UUIDv7. Returns false for other versions.</summary>
        public static bool TryGetTimestamp(string value, out DateTimeOffset timestamp)
        {
            timestamp = default;
            if (!IsValid(value) || value[14] != '7')
            {
                return false;
            }

            string hex = value.Substring(0, 8) + value.Substring(9, 4);
            long ms = Convert.ToInt64(hex, 16);
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return true;
        }

        private static string Format(byte[] b)
        {
            const string digits = "0123456789abcdef";
            var chars = new char[36];
            int pos = 0;
            for (int i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10)
                {
                    chars[pos++] = '-';
                }

                chars[pos++] = digits[b[i] >> 4];
                chars[pos++] = digits[b[i] & 0x0F];
            }

            return new string(chars);
        }
    }
}
