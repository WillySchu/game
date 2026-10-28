using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RaidZero.Shared.Ids;
using RaidZero.Shared.Inventory;

namespace RaidZero.Shared.Raid
{
    /// <summary>
    /// Turns <see cref="RaidMessage"/>s into bytes and back. The network layer carries the bytes untouched, so
    /// the whole protocol, including what happens with hostile input, is plain C# that tests can reach.
    ///
    /// Layout: a version byte, a type byte, then the message's fields, little-endian. Item IDs travel as their
    /// 16 raw bytes rather than 36 characters. A container travels as a flat list of its items in depth-first
    /// order, each naming its parent's position in the list, which is compact and can't describe a cycle.
    ///
    /// Decoding never trusts its input: every length and count is capped, IDs must be valid UUIDs, and
    /// anything malformed, truncated, oversized or with bytes left over throws <see cref="ProtocolException"/>.
    /// </summary>
    public static class RaidProtocol
    {
        public const byte Version = 1;

        /// <summary>The largest message a client may send. Requests are small; this leaves plenty of room.</summary>
        public const int MaxClientMessageBytes = 2048;

        /// <summary>The largest message the server sends: a big container view.</summary>
        public const int MaxServerMessageBytes = 256 * 1024;

        public const int MaxOpsPerRequest = 16;
        public const int MaxItemsPerView = 4096;
        public const int MaxStringBytes = 64;

        private const byte NullString = 0xFF;
        private const ushort NoParent = 0xFFFF;

        private enum MessageType : byte
        {
            InventoryRequest = 1,
            PickUp = 2,
            Drop = 3,
            Open = 4,
            Close = 5,
            RequestResult = 64,
            ContainerView = 65,
            ContainerClosed = 66,
        }

        private enum OpKind : byte
        {
            Move = 1,
            Split = 2,
            Merge = 3,
            Discard = 4,
        }

        public static byte[] Encode(RaidMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Version);
                switch (message)
                {
                    case InventoryRequest request:
                        writer.Write((byte)MessageType.InventoryRequest);
                        writer.Write(request.RequestId);
                        WriteOps(writer, request.Ops);
                        break;
                    case PickUpRequest request:
                        writer.Write((byte)MessageType.PickUp);
                        writer.Write(request.RequestId);
                        WriteId(writer, request.ItemId);
                        break;
                    case DropRequest request:
                        writer.Write((byte)MessageType.Drop);
                        writer.Write(request.RequestId);
                        WriteId(writer, request.ItemId);
                        break;
                    case OpenRequest request:
                        writer.Write((byte)MessageType.Open);
                        writer.Write(request.RequestId);
                        WriteId(writer, request.ContainerId);
                        break;
                    case CloseRequest request:
                        writer.Write((byte)MessageType.Close);
                        WriteId(writer, request.ContainerId);
                        break;
                    case RequestResult result:
                        writer.Write((byte)MessageType.RequestResult);
                        writer.Write(result.RequestId);
                        writer.Write((byte)((result.Success ? 1 : 0) | (result.FailedOpIndex.HasValue ? 2 : 0)));
                        WriteString(writer, result.Error);
                        if (result.FailedOpIndex.HasValue)
                        {
                            writer.Write(checked((byte)result.FailedOpIndex.Value));
                        }

                        break;
                    case ContainerView view:
                        writer.Write((byte)MessageType.ContainerView);
                        writer.Write((byte)view.Kind);
                        WriteTree(writer, view.Root);
                        break;
                    case ContainerClosed closed:
                        writer.Write((byte)MessageType.ContainerClosed);
                        WriteId(writer, closed.ContainerId);
                        break;
                    default:
                        throw new ArgumentException("Unknown message type " + message.GetType().Name + ".", nameof(message));
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>Decodes what a client sent. Only client messages are accepted, within the client's size limit.</summary>
        public static RaidMessage DecodeFromClient(byte[] data)
        {
            var message = Decode(data, MaxClientMessageBytes);
            if (!(message is RaidRequest) && !(message is CloseRequest))
            {
                throw new ProtocolException("Clients can't send " + message.GetType().Name + ".");
            }

            return message;
        }

        /// <summary>Decodes what the server sent.</summary>
        public static RaidMessage DecodeFromServer(byte[] data)
        {
            var message = Decode(data, MaxServerMessageBytes);
            if (message is RaidRequest || message is CloseRequest)
            {
                throw new ProtocolException("The server doesn't send " + message.GetType().Name + ".");
            }

            return message;
        }

        private static RaidMessage Decode(byte[] data, int maxBytes)
        {
            if (data == null || data.Length < 2)
            {
                throw new ProtocolException("The message is empty.");
            }

            if (data.Length > maxBytes)
            {
                throw new ProtocolException("The message is " + data.Length + " bytes; the limit is " + maxBytes + ".");
            }

            if (data[0] != Version)
            {
                throw new ProtocolException("Protocol version " + data[0] + " isn't supported; this build speaks version " + Version + ".");
            }

            try
            {
                using (var stream = new MemoryStream(data, 2, data.Length - 2, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    RaidMessage message;
                    switch ((MessageType)data[1])
                    {
                        case MessageType.InventoryRequest:
                            message = new InventoryRequest { RequestId = reader.ReadUInt32(), Ops = ReadOps(reader) };
                            break;
                        case MessageType.PickUp:
                            message = new PickUpRequest { RequestId = reader.ReadUInt32(), ItemId = ReadId(reader) };
                            break;
                        case MessageType.Drop:
                            message = new DropRequest { RequestId = reader.ReadUInt32(), ItemId = ReadId(reader) };
                            break;
                        case MessageType.Open:
                            message = new OpenRequest { RequestId = reader.ReadUInt32(), ContainerId = ReadId(reader) };
                            break;
                        case MessageType.Close:
                            message = new CloseRequest { ContainerId = ReadId(reader) };
                            break;
                        case MessageType.RequestResult:
                            message = ReadResult(reader);
                            break;
                        case MessageType.ContainerView:
                            message = new ContainerView { Kind = ReadKind(reader), Root = ReadTree(reader) };
                            break;
                        case MessageType.ContainerClosed:
                            message = new ContainerClosed { ContainerId = ReadId(reader) };
                            break;
                        default:
                            throw new ProtocolException("Unknown message type " + data[1] + ".");
                    }

                    if (stream.Position != stream.Length)
                    {
                        throw new ProtocolException((stream.Length - stream.Position) + " unexpected bytes after the message.");
                    }

                    return message;
                }
            }
            catch (EndOfStreamException)
            {
                throw new ProtocolException("The message ends early.");
            }
        }

        // ---- Ops ----

        private static void WriteOps(BinaryWriter writer, List<InventoryOp> ops)
        {
            if (ops == null || ops.Count == 0 || ops.Count > MaxOpsPerRequest)
            {
                throw new ArgumentException("A request needs 1 to " + MaxOpsPerRequest + " ops.");
            }

            writer.Write((byte)ops.Count);
            foreach (var op in ops)
            {
                switch (op?.Op)
                {
                    case InventoryOpKinds.Move:
                        writer.Write((byte)OpKind.Move);
                        WriteId(writer, op.ItemId);
                        WriteTarget(writer, op.To);
                        break;
                    case InventoryOpKinds.Split:
                        writer.Write((byte)OpKind.Split);
                        WriteId(writer, op.ItemId);
                        WriteTarget(writer, op.To);
                        writer.Write(op.Quantity);
                        break;
                    case InventoryOpKinds.Merge:
                        writer.Write((byte)OpKind.Merge);
                        WriteId(writer, op.ItemId);
                        WriteId(writer, op.IntoId);
                        writer.Write(op.Quantity);
                        break;
                    case InventoryOpKinds.Discard:
                        writer.Write((byte)OpKind.Discard);
                        WriteId(writer, op.ItemId);
                        break;
                    default:
                        throw new ArgumentException("Unknown op '" + op?.Op + "'.");
                }
            }
        }

        private static List<InventoryOp> ReadOps(BinaryReader reader)
        {
            int count = reader.ReadByte();
            if (count == 0 || count > MaxOpsPerRequest)
            {
                throw new ProtocolException("A request needs 1 to " + MaxOpsPerRequest + " ops; this one has " + count + ".");
            }

            var ops = new List<InventoryOp>(count);
            for (int i = 0; i < count; i++)
            {
                var kind = (OpKind)reader.ReadByte();
                var itemId = ReadId(reader);
                switch (kind)
                {
                    case OpKind.Move:
                        ops.Add(InventoryOp.Move(itemId, ReadTarget(reader)));
                        break;
                    case OpKind.Split:
                        var to = ReadTarget(reader);
                        ops.Add(InventoryOp.Split(itemId, reader.ReadInt32(), to));
                        break;
                    case OpKind.Merge:
                        var intoId = ReadId(reader);
                        ops.Add(InventoryOp.Merge(itemId, intoId, reader.ReadInt32()));
                        break;
                    case OpKind.Discard:
                        ops.Add(InventoryOp.Discard(itemId));
                        break;
                    default:
                        throw new ProtocolException("Unknown op kind " + (byte)kind + ".");
                }
            }

            return ops;
        }

        private static void WriteTarget(BinaryWriter writer, InventoryTarget to)
        {
            if (to == null)
            {
                throw new ArgumentException("Move and split need a target.");
            }

            bool hasCell = to.X.HasValue && to.Y.HasValue;
            WriteId(writer, to.ParentId);
            WriteString(writer, to.Slot);
            writer.Write((byte)((hasCell ? 1 : 0) | (to.Rotated ? 2 : 0)));
            if (hasCell)
            {
                writer.Write(checked((ushort)to.X.Value));
                writer.Write(checked((ushort)to.Y.Value));
            }
        }

        private static InventoryTarget ReadTarget(BinaryReader reader)
        {
            var target = new InventoryTarget { ParentId = ReadId(reader), Slot = ReadString(reader) };
            var flags = reader.ReadByte();
            if ((flags & ~3) != 0)
            {
                throw new ProtocolException("Unknown target flags " + flags + ".");
            }

            target.Rotated = (flags & 2) != 0;
            if ((flags & 1) != 0)
            {
                target.X = reader.ReadUInt16();
                target.Y = reader.ReadUInt16();
            }

            return target;
        }

        // ---- Results ----

        private static RequestResult ReadResult(BinaryReader reader)
        {
            var result = new RequestResult { RequestId = reader.ReadUInt32() };
            var flags = reader.ReadByte();
            if ((flags & ~3) != 0)
            {
                throw new ProtocolException("Unknown result flags " + flags + ".");
            }

            result.Success = (flags & 1) != 0;
            result.Error = ReadString(reader);
            if ((flags & 2) != 0)
            {
                result.FailedOpIndex = reader.ReadByte();
            }

            return result;
        }

        private static RootKind ReadKind(BinaryReader reader)
        {
            var kind = (RootKind)reader.ReadByte();
            if (kind < RootKind.Character || kind > RootKind.Ground)
            {
                throw new ProtocolException("Unknown container kind " + (byte)kind + ".");
            }

            return kind;
        }

        // ---- Item trees ----

        private static void WriteTree(BinaryWriter writer, ItemNode root)
        {
            if (root == null)
            {
                throw new ArgumentException("A view needs a container.");
            }

            var flat = new List<(ItemNode Node, int Parent)>();
            var stack = new Stack<(ItemNode Node, int Parent)>();
            stack.Push((root, NoParent));
            while (stack.Count > 0)
            {
                var (node, parent) = stack.Pop();
                int index = flat.Count;
                flat.Add((node, parent));
                if (node.Children != null)
                {
                    for (int i = node.Children.Count - 1; i >= 0; i--)
                    {
                        stack.Push((node.Children[i], index));
                    }
                }
            }

            if (flat.Count > MaxItemsPerView)
            {
                throw new ArgumentException("A view holds at most " + MaxItemsPerView + " items.");
            }

            writer.Write((ushort)flat.Count);
            foreach (var (node, parent) in flat)
            {
                WriteId(writer, node.Id);
                writer.Write((ushort)parent);
                WriteString(writer, node.DefId);
                WriteString(writer, node.Slot);
                byte flags = (byte)((node.X.HasValue ? 1 : 0) | (node.Y.HasValue ? 2 : 0) | (node.Rotated ? 4 : 0) | (node.Durability.HasValue ? 8 : 0));
                writer.Write(flags);
                if (node.X.HasValue)
                {
                    writer.Write(checked((ushort)node.X.Value));
                }

                if (node.Y.HasValue)
                {
                    writer.Write(checked((ushort)node.Y.Value));
                }

                writer.Write(node.Quantity);
                if (node.Durability.HasValue)
                {
                    writer.Write((float)node.Durability.Value);
                }
            }
        }

        private static ItemNode ReadTree(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            if (count == 0 || count > MaxItemsPerView)
            {
                throw new ProtocolException("A view holds 1 to " + MaxItemsPerView + " items; this one has " + count + ".");
            }

            var nodes = new ItemNode[count];
            for (int i = 0; i < count; i++)
            {
                var node = new ItemNode { Id = ReadId(reader) };
                int parent = reader.ReadUInt16();
                node.DefId = ReadString(reader) ?? throw new ProtocolException("Item " + i + " has no def_id.");
                node.Slot = ReadString(reader);
                var flags = reader.ReadByte();
                if ((flags & ~15) != 0)
                {
                    throw new ProtocolException("Unknown item flags " + flags + ".");
                }

                if ((flags & 1) != 0)
                {
                    node.X = reader.ReadUInt16();
                }

                if ((flags & 2) != 0)
                {
                    node.Y = reader.ReadUInt16();
                }

                node.Rotated = (flags & 4) != 0;
                node.Quantity = reader.ReadInt32();
                if ((flags & 8) != 0)
                {
                    node.Durability = reader.ReadSingle();
                }

                // The root comes first and only first; every other item's parent comes before it.
                if (i == 0 ? parent != NoParent : parent >= i)
                {
                    throw new ProtocolException("Item " + i + " has an impossible parent " + parent + ".");
                }

                if (i > 0)
                {
                    nodes[parent].Children.Add(node);
                }

                nodes[i] = node;
            }

            return nodes[0];
        }

        // ---- Primitives ----

        private static void WriteId(BinaryWriter writer, string id)
        {
            if (!Uuid7.IsValid(id))
            {
                throw new ArgumentException("'" + id + "' isn't a canonical lowercase UUID, so it can't be sent.");
            }

            for (int i = 0; i < id.Length; i++)
            {
                if (id[i] == '-')
                {
                    continue;
                }

                writer.Write((byte)((Hex(id[i]) << 4) | Hex(id[i + 1])));
                i++;
            }
        }

        private static string ReadId(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(16);
            if (bytes.Length != 16)
            {
                throw new EndOfStreamException();
            }

            const string digits = "0123456789abcdef";
            var text = new StringBuilder(36);
            for (int i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10)
                {
                    text.Append('-');
                }

                text.Append(digits[bytes[i] >> 4]);
                text.Append(digits[bytes[i] & 15]);
            }

            return text.ToString();
        }

        private static int Hex(char c) => c <= '9' ? c - '0' : c - 'a' + 10;

        private static void WriteString(BinaryWriter writer, string value)
        {
            if (value == null)
            {
                writer.Write(NullString);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > MaxStringBytes)
            {
                throw new ArgumentException("'" + value + "' is longer than " + MaxStringBytes + " bytes.");
            }

            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadByte();
            if (length == NullString)
            {
                return null;
            }

            if (length > MaxStringBytes)
            {
                throw new ProtocolException("A string of " + length + " bytes is over the limit of " + MaxStringBytes + ".");
            }

            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }

            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>A message that doesn't follow the protocol. From a client, that's a bug or a tampered client.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message)
            : base(message)
        {
        }
    }
}
