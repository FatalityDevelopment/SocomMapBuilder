using System.Globalization;
using CodeDesigner.Ps2.FileFormats.Zipper.ZED;

namespace CodeDesigner.Ps2.FileFormats.Zipper.Anim
{
    /// <summary>
    /// ZANIM effect scripts (<c>CZANIM.ZAR</c> in each map package; <c>MPZANIM</c> / <c>UIZANIM</c> /
    /// <c>EXTZANIM.ZAR</c> on disc): muzzle flashes, shell ejection, smoke, explosions, sounds, camera shakes.
    /// Container tree: <c>Anim_Sets › &lt;set&gt; › Name_Table</c> (names scripts refer to), <c>RdrPath_List</c>,
    /// <c>Animation_List › &lt;anim&gt;</c> with <c>Anim_Params</c> (24 B), <c>Name_Index_Table</c> (u16 indices into
    /// the set's names: [0] NA, [1] the animation itself), <c>Node_Ref_List</c> (8-byte object refs: byte 1 = name
    /// index, byte 2 = flags, byte 3 = parent ref) and <c>Seq_Data</c>.
    ///
    /// Seq_Data is a list of tracks that run in parallel, each a 7-word header (target name index, flags 0x0102 /
    /// 0x0104, 0, byte size including the header, 0, 0, 0) and instructions: u16 opcode, u16 (words &lt;&lt; 4 |
    /// flags), then the arguments at byte offsets from the instruction start. The game's opcode table (names and
    /// handlers, EE RAM 0x84F2A0, 20-byte records) gives <see cref="OpNames"/>; argument layouts below were read from
    /// those handlers. Object operands: 0 none, 0xF9 the effect's own object, 0xFA its owner (e.g. the weapon's
    /// firepoint), else an index into the node refs.
    /// </summary>
    public sealed class ZanimFile
    {
        public const int OpIf = 0x02, OpElseIf = 0x03, OpElse = 0x04, OpEndIf = 0x05, OpWhile = 0x27, OpEndWhile = 0x28,
            OpForChildren = 0x29, OpEndForChildren = 0x2A, OpCallAnimation = 0x2D;
        public const int ObjectSelf = 0xF9, ObjectOwner = 0xFA;

        /// <summary>Opcode names from the game's own table.</summary>
        public static readonly IReadOnlyDictionary<int, string> OpNames = new Dictionary<int, string>
        {
            [0x00] = "Reserved", [0x01] = "QUAD_ALIGN", [0x02] = "IF", [0x03] = "ELSEIF", [0x04] = "ELSE", [0x05] = "ENDIF",
            [0x06] = "OBJECT_MAKE_WORLDCHILD", [0x07] = "NODE_ACTIVE", [0x08] = "NODE_RENDERED", [0x09] = "RANGE_TEST",
            [0x0A] = "RANDOM_WEIGHT", [0x0B] = "FAIL", [0x0C] = "ANIM_HEALTH", [0x0D] = "ANIM_LOD", [0x0E] = "LOOP", [0x0F] = "WAIT",
            [0x10] = "OBJECT_BLENDMODE", [0x11] = "OBJECT_ACTIVE_STATE", [0x12] = "OBJECT_TRANSLATE_STATE", [0x13] = "OBJECT_ROTATE_STATE",
            [0x14] = "OBJECT_AIM", [0x15] = "OBJECT_MOTION", [0x16] = "OBJECT_MOTION_FROM_TO", [0x17] = "WHEEL_MOTION",
            [0x18] = "SCROLL_MOTION", [0x19] = "OBJECT_OPACITY_FROM_TO", [0x1A] = "OBJECT_MOTION_SI_SCRIPT", [0x1B] = "PARTICLE_SOURCE",
            [0x1C] = "CAMERA", [0x1D] = "DESTRUCTION_SOURCE", [0x1E] = "SOUND", [0x1F] = "UPDATE_SHADOW_MAP_LIGHTING", [0x20] = "LIGHT",
            [0x21] = "BLUR3D", [0x22] = "IRIS_EFFECT", [0x23] = "SCALE_COLOR", [0x24] = "TRUE_COLOR_SCALE", [0x25] = "RIPPLE",
            [0x26] = "RIPPLE_NODE_GROUP", [0x27] = "WHILE", [0x28] = "END_WHILE", [0x29] = "FOR_CHILDREN", [0x2A] = "END_FOR_CHILDREN",
            [0x2B] = "EXPRESSION", [0x2C] = "BREAK", [0x2D] = "CALL_ANIMATION", [0x2E] = "STOP_ANIMATION", [0x2F] = "PAUSE_ANIMATION",
            [0x30] = "RESUME_ANIMATION", [0x31] = "INVALIDATE_ANIMATION", [0x32] = "CALL_SEQUENCE", [0x33] = "STOP_SEQUENCE",
            [0x34] = "DEBUG", [0x35] = "OBJECT_ADD_CHILD", [0x36] = "OBJECT_DELETE_CHILD", [0x37] = "MESSAGE", [0x38] = "TIMER",
            [0x39] = "FIRE_WEAPON", [0x3A] = "REMOVE_SATCHELS", [0x3B] = "RESET_BODY_PARTS", [0x3C] = "BODY_FALL_ON_MATERIAL_SOUND",
            [0x3D] = "VALVE", [0x3E] = "VBIT", [0x3F] = "VWATCH", [0x40] = "DYNAMICS_RELEASE_CAMERA", [0x41] = "DYNAMICS_ACQUIRE_CAMERA",
            [0x42] = "CAMERA_INDOORS", [0x43] = "SET_CAMERA_REGION_TEST", [0x44] = "GET_CAMERA_REGION_TEST", [0x45] = "CAMERA_PARAMS",
            [0x46] = "CAMERA_3RD_PERSON", [0x47] = "CAMERA_SHAKE", [0x48] = "PLAYER_INDOORS", [0x49] = "PLAYER_CONTROLS",
            [0x4A] = "IS_PLAYER_NODE", [0x4B] = "WIND", [0x4C] = "HUD_ON", [0x4D] = "HUD_OFF", [0x4E] = "HUD_LETTERBOX_ON",
            [0x4F] = "HUD_LETTERBOX_OFF", [0x50] = "HUD_TRIGGER_MISSION_CAM", [0x51] = "HUD_START_DEFUSE_TIMER",
            [0x52] = "HUD_STOP_DEFUSE_TIMER", [0x53] = "HUD_TRIGGER_POPUP", [0x54] = "NVG_NOISE", [0x55] = "START_IMAGE_RECORDER",
            [0x56] = "STOP_IMAGE_RECORDER", [0x57] = "EXECUTE_ACTION", [0x58] = "REMOVE_DECALS",
        };

        public static string OpName(int op) => OpNames.TryGetValue(op, out var n) ? n : $"OP_{op:X}";

        public sealed record NodeRef(string Name, int NameIndex, int Flags, int Parent);

        /// <summary>
        /// One instruction. <see cref="Depth"/> is its nesting (IF / WHILE / FOR_CHILDREN bodies are one deeper);
        /// <see cref="Condition"/> marks the condition instructions inside an IF / ELSEIF / WHILE. <see cref="Fields"/>
        /// holds the decoded arguments for the opcodes read so far; <see cref="Words"/> is everything as stored.
        /// </summary>
        public sealed record Instruction(int Op, string Name, int Flags, int Depth, bool Condition, uint[] Words, Dictionary<string, object> Fields);

        public sealed record Track(string Target, int TargetIndex, uint HeaderFlags, List<Instruction> Instructions);

        public sealed class Animation
        {
            public string Name { get; init; } = string.Empty;
            public string Set { get; init; } = string.Empty;
            public byte[] Params { get; init; } = Array.Empty<byte>();
            /// <summary>The names this animation uses ([0] NA, [1] itself).</summary>
            public List<string> Names { get; init; } = new();
            public List<NodeRef> NodeRefs { get; init; } = new();
            public byte[] Sequence { get; init; } = Array.Empty<byte>();
            public List<Track> Tracks { get; } = new();

            /// <summary>Animations this one starts (CALL_ANIMATION), by name.</summary>
            public IEnumerable<string> Spawns => Tracks.SelectMany(t => t.Instructions)
                .Where(i => i.Op == OpCallAnimation && i.Fields.TryGetValue("animation", out var a) && a is string)
                .Select(i => (string)i.Fields["animation"]);

            public string NameAt(int index) => index >= 0 && index < Names.Count ? Names[index] : $"#{index}";

            /// <summary>An object operand: none / self / owner / a node ref's name.</summary>
            public string ObjectName(int o) => o switch
            {
                0 => "none",
                ObjectSelf => "self",
                ObjectOwner => "owner",
                _ => o < NodeRefs.Count ? NodeRefs[o].Name : $"ref#{o}",
            };
        }

        public List<Animation> Animations { get; } = new();

        public Animation? Find(string name) => Animations.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public static ZanimFile Parse(byte[] data)
        {
            var file = new ZanimFile();
            var c = ZedContainer.FromBytes(data);
            var nodes = c.Nodes;
            int sets = -1;
            for (int i = 0; i < nodes.Count; i++) if (nodes[i].Name == "Anim_Sets") { sets = i; break; }
            if (sets < 0) throw new InvalidDataException("Not a ZANIM archive (no Anim_Sets)");
            foreach (int set in ZedTree.Children(nodes, sets))
            {
                var kids = ZedTree.Children(nodes, set).ToDictionary(k => nodes[k].Name, k => k);
                var names = kids.TryGetValue("Name_Table", out int nt) ? ZedTree.Children(nodes, nt).Select(k => nodes[k].Name).ToList() : new List<string>();
                if (!kids.TryGetValue("Animation_List", out int al)) continue;
                foreach (int a in ZedTree.Children(nodes, al))
                {
                    var parts = ZedTree.Children(nodes, a).ToDictionary(k => nodes[k].Name, k => nodes[k].Data);
                    byte[] index = parts.GetValueOrDefault("Name_Index_Table") ?? Array.Empty<byte>();
                    var used = new List<string>();
                    for (int k = 0; k + 1 < index.Length; k += 2)
                    {
                        int n = BitConverter.ToUInt16(index, k);
                        used.Add(n < names.Count ? names[n] : $"#{n}");
                    }
                    byte[] refBytes = parts.GetValueOrDefault("Node_Ref_List") ?? Array.Empty<byte>();
                    var refs = new List<NodeRef>();
                    for (int k = 0; k + 8 <= refBytes.Length; k += 8)
                    {
                        int ni = refBytes[k + 1];
                        refs.Add(new NodeRef(ni < used.Count ? used[ni] : $"#{ni}", ni, refBytes[k + 2], refBytes[k + 3]));
                    }
                    var anim = new Animation
                    {
                        Name = nodes[a].Name, Set = nodes[set].Name, Params = parts.GetValueOrDefault("Anim_Params") ?? Array.Empty<byte>(),
                        Names = used, NodeRefs = refs, Sequence = parts.GetValueOrDefault("Seq_Data") ?? Array.Empty<byte>(),
                    };
                    DecodeTracks(anim);
                    file.Animations.Add(anim);
                }
            }
            return file;
        }

        private static void DecodeTracks(Animation a)
        {
            byte[] b = a.Sequence;
            int o = 0;
            while (o + 28 <= b.Length)
            {
                int target = (int)BitConverter.ToUInt32(b, o);
                uint headerFlags = BitConverter.ToUInt32(b, o + 4);
                int size = (int)BitConverter.ToUInt32(b, o + 12);
                if (size < 28 || o + size > b.Length) break;
                var list = new List<Instruction>();
                var conditionEnds = new Stack<int>();
                int depth = 0;
                int p = o + 28, end = o + size;
                while (p + 4 <= end)
                {
                    while (conditionEnds.Count > 0 && p >= conditionEnds.Peek()) conditionEnds.Pop();
                    int op = BitConverter.ToUInt16(b, p);
                    int sizeWord = BitConverter.ToUInt16(b, p + 2);
                    int words = sizeWord >> 4;
                    if (words < 1 || p + words * 4 > end) break;
                    // IF / ELSEIF / WHILE: the declared size covers their condition instructions, which follow.
                    bool conditional = op is OpIf or OpElseIf or OpWhile;
                    int own = conditional ? Math.Min(2, words) : words;
                    if (op is OpElseIf or OpElse or OpEndIf or OpEndWhile or OpEndForChildren) depth = Math.Max(0, depth - 1);
                    var w = new uint[own];
                    for (int k = 0; k < own; k++) w[k] = BitConverter.ToUInt32(b, p + 4 * k);
                    bool inCondition = conditionEnds.Count > 0;
                    list.Add(new Instruction(op, OpName(op), sizeWord & 0xF, depth + conditionEnds.Count, inCondition, w, Decode(a, op, b, p, own * 4)));
                    if (op is OpIf or OpElseIf or OpElse or OpWhile or OpForChildren) depth++;
                    if (conditional) conditionEnds.Push(p + words * 4);
                    p += own * 4;
                }
                a.Tracks.Add(new Track(a.NameAt(target), target, headerFlags, list));
                o += size;
            }
        }

        // Argument layouts read from the game's handlers (byte offsets from the instruction start).
        private static Dictionary<string, object> Decode(Animation a, int op, byte[] b, int p, int length)
        {
            var f = new Dictionary<string, object>();
            int end = p + length;
            bool Has(int off, int n) => p + off + n <= end;
            int U16(int off) => Has(off, 2) ? BitConverter.ToUInt16(b, p + off) : 0;
            int U8(int off) => Has(off, 1) ? b[p + off] : 0;
            int I32(int off) => Has(off, 4) ? BitConverter.ToInt32(b, p + off) : 0;
            float F(int off) => Has(off, 4) ? BitConverter.ToSingle(b, p + off) : 0f;
            float[] V(int off) => new[] { F(off), F(off + 4), F(off + 8) };
            switch (op)
            {
                case 0x0A: // RANDOM_WEIGHT: true when rand() <= weight
                    f["weight"] = F(4);
                    break;
                case 0x0E: // LOOP: flag 1 = count (-1 forever), flag 2 = seconds
                {
                    int flags = I32(4);
                    if ((flags & 1) != 0) f["count"] = I32(8);
                    if ((flags & 2) != 0) f["seconds"] = F(8);
                    f["flags"] = flags;
                    break;
                }
                case 0x0F: // WAIT: flag 8 seconds, 0x10 frames, 0x20 + random range at +0xC; flags 2 / 4 pick the clock
                {
                    int flags = U16(4);
                    f["flags"] = flags;
                    if ((flags & 0x10) != 0) f["frames"] = I32(8); else f["seconds"] = F(8);
                    if ((flags & 0x20) != 0 && Has(0xC, 4)) f["random"] = (flags & 0x10) != 0 ? I32(0xC) : F(0xC);
                    break;
                }
                case 0x11: // OBJECT_ACTIVE_STATE
                    f["object"] = a.ObjectName(U16(6));
                    f["active"] = U16(4) == 1;
                    break;
                case 0x12: // OBJECT_TRANSLATE_STATE
                case 0x13: // OBJECT_ROTATE_STATE: flag 1 set Euler, 2 add Euler, 4 copy the reference locally
                {
                    int flags = U16(4);
                    f["flags"] = flags;
                    f["object"] = a.ObjectName(U8(6));
                    if (U8(7) != 0) f["reference"] = a.ObjectName(U8(7));
                    f[op == 0x13 ? "euler" : "offset"] = V(8);
                    break;
                }
                case 0x16: // OBJECT_MOTION_FROM_TO: duration +0x34; flag 0x200 scale record at u16(+0x30): start, final, rate/s
                {
                    int flags = U16(4);
                    f["flags"] = flags;
                    f["object"] = a.ObjectName(U8(6));
                    f["duration"] = F(0x34);
                    int sc = U16(0x30);
                    if ((flags & 0x200) != 0 && sc > 0 && p + sc + 36 <= b.Length)
                    {
                        float S(int k) => BitConverter.ToSingle(b, p + sc + 4 * k);
                        f["scaleStart"] = new[] { S(0), S(1), S(2) };
                        f["scaleEnd"] = new[] { S(3), S(4), S(5) };
                        f["scaleRate"] = new[] { S(6), S(7), S(8) };
                    }
                    break;
                }
                case 0x20: // LIGHT: object, colour (0-255), range, time (tentative beyond colour)
                    f["flags"] = U16(4);
                    f["object"] = a.ObjectName(U8(6));
                    f["color"] = V(0x20);
                    f["range"] = F(0x2C);
                    f["time"] = F(0x3C);
                    break;
                case OpCallAnimation: // flags (0x200 = wait for it), object, animation name
                    f["flags"] = U16(4);
                    f["object"] = a.ObjectName(U8(6));
                    f["animation"] = a.NameAt(U8(7));
                    f["waitForEnd"] = (U16(4) & 0x200) != 0;
                    break;
                case 0x32: // CALL_SEQUENCE / STOP_SEQUENCE: track (sequence) index
                case 0x33:
                    f["sequence"] = I32(4);
                    break;
                case OpIf:
                case OpElseIf:
                case OpWhile:
                    f["terms"] = I32(4);
                    break;
            }
            return f;
        }

        /// <summary>A value of <see cref="Instruction.Fields"/> as text (for listings).</summary>
        public static string Format(object v) => v switch
        {
            float x => x.ToString("0.####", CultureInfo.InvariantCulture),
            float[] xs => "(" + string.Join(", ", xs.Select(x => x.ToString("0.####", CultureInfo.InvariantCulture))) + ")",
            _ => v.ToString() ?? string.Empty,
        };
    }
}
