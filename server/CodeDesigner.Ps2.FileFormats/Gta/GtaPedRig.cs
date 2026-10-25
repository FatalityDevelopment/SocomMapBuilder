namespace CodeDesigner.Ps2.FileFormats.Gta
{
    /// <summary>
    /// The San Andreas ped skeleton against SOCOM II's alg_UN01 (for retargeting peds and their clips): every ped bone
    /// and the SOCOM II bone it skins to, and the ped joint each SOCOM II bone sits on. Both rigs face -Z with Y up once
    /// the ped is built (<see cref="GtaPackage"/>) and run each limb bone's x axis along the limb.
    /// </summary>
    public static class GtaPedRig
    {
        public static readonly Dictionary<string, string> BoneMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Root"] = "hips", ["Ripped"] = "hips", ["Pelvis"] = "hips", ["Spine"] = "spinelo", ["Spine 1"] = "spinelo", ["Belly"] = "spinelo",
            ["Spine1"] = "spinehi", ["Spine 2"] = "spinehi",
            ["L breast"] = "spinehi", ["R breast"] = "spinehi", ["Neck"] = "neck", ["Head"] = "head", ["Jaw"] = "head", ["L Brow"] = "head", ["R Brow"] = "head",
            ["Bip01 R Clavicle"] = "rscap", ["R UpperArm"] = "rbicep", ["R ForeArm"] = "rforearm", ["R Hand"] = "rhand", ["R Finger"] = "rhand", ["R Finger01"] = "rhand",
            ["Bip01 L Clavicle"] = "lscap", ["L UpperArm"] = "lbicep", ["L ForeArm"] = "lforearm", ["L Hand"] = "lhand", ["L Finger"] = "lhand", ["L Finger01"] = "lhand",
            ["R Thigh"] = "rthigh", ["R Calf"] = "rcalf", ["R Foot"] = "rfoot", ["R Toe0"] = "rtoe",
            ["L Thigh"] = "lthigh", ["L Calf"] = "lcalf", ["L Foot"] = "lfoot", ["L Toe0"] = "ltoe",
        };

        public static readonly Dictionary<string, string> Anchors = new()
        {
            ["hips"] = "Pelvis", ["spinelo"] = "Spine|Spine 1", ["spinehi"] = "Spine1|Spine 2", ["neck"] = "Neck", ["head"] = "Head",
            ["rscap"] = "Bip01 R Clavicle", ["rbicep"] = "R UpperArm", ["rforearm"] = "R ForeArm", ["rhand"] = "R Hand",
            ["lscap"] = "Bip01 L Clavicle", ["lbicep"] = "L UpperArm", ["lforearm"] = "L ForeArm", ["lhand"] = "L Hand",
            ["rthigh"] = "R Thigh", ["rcalf"] = "R Calf", ["rfoot"] = "R Foot", ["rtoe"] = "R Toe0",
            ["lthigh"] = "L Thigh", ["lcalf"] = "L Calf", ["lfoot"] = "L Foot", ["ltoe"] = "L Toe0",
        };
    }
}
