namespace CodeDesigner.Ps2.FileFormats.Zipper.Ca
{
    /// <summary>
    /// CA character bones: names are compiled out, nodes and animation tracks carry CRC32 name hashes. The known ones
    /// (Mods/SOCOMCA/ca_archive.BONES) with the SOCOM II bone each corresponds to. CA limbs are camelCase
    /// (rightShoulder ...); core bones keep the SOCOM II names. Unknown hashes are named <c>bone_&lt;hash&gt;</c>.
    /// </summary>
    public static class CaBones
    {
        public static readonly IReadOnlyDictionary<uint, (string Name, string Socom2)> Known = new Dictionary<uint, (string, string)>
        {
            [0x89ED856D] = ("skel_root", "body"),
            [0x7EE3A92F] = ("hips", "hips"),
            [0x5E4CDCAC] = ("spinelo", "spinelo"),
            [0xD343BC9D] = ("spinehi", "spinehi"),
            [0x2011D653] = ("neck", "neck"),
            [0xA7F3F69C] = ("head", "head"),
            [0xFC45C9E2] = ("headTop", "head"),
            [0x0886299D] = ("headPiece", "head"),
            [0x14DD7537] = ("headPiece2", "head"),
            [0xDBA80BB2] = ("body", "body"),
            [0xA36E7E68] = ("rightCollar", "rscap"),
            [0x45780C10] = ("rightShoulder", "rbicep"),
            [0x9586FDE0] = ("rightElbow", "rforearm"),
            [0x4C4BFAA8] = ("rightWrist", "rforearm"),
            [0xF708CF93] = ("rightHand", "rhand"),
            [0x5078BD30] = ("leftCollar", "lscap"),
            [0x3577BB50] = ("leftShoulder", "lbicep"),
            [0x3A34EDF2] = ("leftElbow", "lforearm"),
            [0xE3F9EABA] = ("leftWrist", "lforearm"),
            [0x4F03913A] = ("leftHand", "lhand"),
            [0x0EE0BC99] = ("rightThigh", "rthigh"),
            [0x7A12CE1D] = ("rightKnee", "rcalf"),
            [0x19E5B48F] = ("rightFoot", "rfoot"),
            [0xA152AC8B] = ("leftThigh", "lthigh"),
            [0xC21990B4] = ("leftKnee", "lcalf"),
            [0xA1EEEA26] = ("leftFoot", "lfoot"),
            // Clip-only bones (not in the meshes' skeletons): the weapon mount under rightHand, toes under the feet.
            [0xBBD4FAD5] = ("rifle", "rifle"),
            [0x799F10C4] = ("rightToe", "rtoe"),
            [0xCF64B6F3] = ("leftToe", "ltoe"),
        };

        public static string Name(uint hash) => Known.TryGetValue(hash, out var b) ? b.Name : $"bone_{hash:x8}";

        /// <summary>The hash a bone name stands for: a known name, or <c>bone_&lt;hash&gt;</c>.</summary>
        public static uint? Hash(string name)
        {
            foreach (var (h, b) in Known) if (b.Name == name) return h;
            return name.StartsWith("bone_") && uint.TryParse(name[5..], System.Globalization.NumberStyles.HexNumber, null, out uint v) ? v : null;
        }
    }
}
