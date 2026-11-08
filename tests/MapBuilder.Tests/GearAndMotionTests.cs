using CodeDesigner.Ps2.FileFormats.Zipper.RDR;
using Xunit;
using static MapBuilder.Tests.Fixtures.RdrBuilder;

namespace MapBuilder.Tests;

/// <summary>
/// The worn-gear table (READERC.ZAR › character.rdr's root <c>gear</c>: each item's model, bone and offset) and
/// motion.rdr (each clip's flags, values and timed ZANIM callbacks), which the Animation Editor and the Map Builder's
/// figures use to dress characters and to show the smoking guard's cigarette. Synthetic data shaped like retail.
/// </summary>
public sealed class GearAndMotionTests
{
    [Fact]
    public void Gear_rows_give_model_bone_and_offset_and_share_value_blocks()
    {
        // Retail writes an offset once and points later rows at it (flak_helmet's ofs is ["head", shared, shared]).
        var zero = L(0, 0, 0);
        var upright = L(44.8f, 90, -44.8f);
        byte[] rdr = Build(L(Obj(
            ("AngleUnits", "DEG"),
            ("gear", L(
                Obj(("name", "seal_des_hat"), ("model", "seal_des_hat.flt"), ("ofs", L("head", zero, upright))),
                Obj(("name", "flak_helmet"), ("model", "flak_helmet_maya.mb"), ("ofs", L("head", zero, upright))),
                Obj(("name", "cigarette"), ("model", "cigarette.flt"), ("ofs", L("lhand", L(1.75f, -0.12f, -0.33f), L(2.93f, -132.2f, 159.5f)))),
                Obj(("name", "bare"), ("model", "bare.flt")))),
            ("characters", L(
                "guard_alb", Obj(("default_gear", L("cigarette", "flak_helmet"))))))));

        var types = CharacterTypes.Parse(rdr);
        Assert.False(types.Radians);
        Assert.Equal(new[] { "seal_des_hat", "flak_helmet", "cigarette", "bare" }, types.GearTable.Select(g => g.Name));

        var helmet = types.GearItemOf("FLAK_HELMET")!;
        Assert.Equal("flak_helmet_maya", helmet.Model);
        Assert.Equal("head", helmet.Bone);
        Assert.Equal(new[] { 0f, 0f, 0f }, helmet.Position);
        Assert.Equal(new[] { 44.8f, 90f, -44.8f }, helmet.Rotation);

        var cig = types.GearItemOf("cigarette")!;
        Assert.Equal("lhand", cig.Bone);
        Assert.Equal(1.75f, cig.Position[0], 3);
        Assert.Equal(-132.2f, cig.Rotation[1], 3);

        // No offset: on the hips, unturned.
        var bare = types.GearItemOf("bare")!;
        Assert.Equal("hips", bare.Bone);
        Assert.Equal(new[] { 0f, 0f, 0f }, bare.Rotation);

        Assert.Equal(new[] { "cigarette", "flak_helmet" }, types.Resolve("guard_alb")!.Gear);
    }

    [Fact]
    public void Eyeballs_are_left_off()
    {
        static CharacterTypes.GearItem Item(string name, string model) => new(name, model, "head", new float[3], new float[3]);
        Assert.True(MapBuilder.Server.Services.ZipperAssetService.IsEyeball(Item("seal_A_left_eye", "left_eye.flt")));
        Assert.True(MapBuilder.Server.Services.ZipperAssetService.IsEyeball(Item("left_eye_blue", "right_eye_blue.flt")));
        Assert.True(MapBuilder.Server.Services.ZipperAssetService.IsEyeball(Item("seal_A_right_eye", "seal_eye.flt")));
        Assert.False(MapBuilder.Server.Services.ZipperAssetService.IsEyeball(Item("seal_des_hat", "seal_des_hat.flt")));
        Assert.False(MapBuilder.Server.Services.ZipperAssetService.IsEyeball(Item("goggles", "goggles.flt")));
    }

    [Fact]
    public void A_weapons_gear_is_worn_only_by_a_type_that_carries_the_weapon()
    {
        var weapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Satchel", "M4A1" };
        var satchel = new CharacterTypes.GearItem("Satchel", "Satchel.flt", "spinehi", new[] { -1.287f, -1.333f, 0f }, new[] { 45.2f, 90f, -45.2f });
        var hat = new CharacterTypes.GearItem("seal_black_hat", "seal_black_hat.flt", "head", new float[3], new float[3]);
        var informant = new CharacterTypes.Resolved { Name = "albanian_informant", Gear = new[] { "Satchel" } };
        var demo = new CharacterTypes.Resolved { Name = "jungle_A51", Weapons = new[] { new CharacterTypes.Weapon("SATCHEL", 2, null) } };

        Assert.False(MapBuilder.Server.Services.ZipperAssetService.Wears(informant, satchel, weapons));
        Assert.True(MapBuilder.Server.Services.ZipperAssetService.Wears(demo, satchel, weapons));
        Assert.True(MapBuilder.Server.Services.ZipperAssetService.Wears(informant, hat, weapons));
    }

    [Fact]
    public void Motion_rows_give_flags_values_and_callbacks_in_time_order()
    {
        byte[] rdr = Build(L(Obj(("animations", L(
            Obj(("anim_name", "seal_stand"), ("BlendTime", L(0.5f)), ("looped", L(1)), ("zanim_callback", Obj(("name", "cold_breath"), ("time", L(0.1f))))),
            Obj(("anim_name", "guard_light_cigarette"), ("SINGLEPLAYER", L()), ("NoFire", L()), ("looped", L(0)), ("playback", L(8)),
                ("zanim_callback", Obj(("name", "cigarette_flame"), ("time", L(0.656f)))),
                ("zanim_callback", Obj(("name", "show_cigarette"), ("time", L(0.05f))))),
            Obj(("looped", L(1))))))));

        var table = MotionTable.Parse(rdr);
        Assert.Equal(2, table.Entries.Count);

        var light = table.Get("GUARD_LIGHT_CIGARETTE")!;
        Assert.Equal(new[] { "SINGLEPLAYER", "NoFire" }, light.Flags);
        Assert.Equal(0f, light.Values["looped"]);
        Assert.Equal(8f, light.Values["playback"]);
        Assert.Equal(new[] { "show_cigarette", "cigarette_flame" }, light.Callbacks.Select(c => c.Effect));
        Assert.Equal(0.05f, light.Callbacks[0].Time, 3);

        var stand = table.Get("seal_stand")!;
        Assert.Empty(stand.Flags);
        Assert.Equal(0.5f, stand.Values["BlendTime"], 3);
        Assert.Equal("cold_breath", stand.Callbacks.Single().Effect);

        Assert.Null(table.Get("nobody"));
    }
}
