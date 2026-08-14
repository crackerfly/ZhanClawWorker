using System.Windows.Media;

namespace ZhanClawControl.Views.Dialogs;

/// <summary>
/// Dialog-only Phosphor Core 2.1.1 Duotone geometry. Warning, error and success
/// reuse the shared catalog; these two semantic icons are unique to dialogs.
/// </summary>
internal static class DialogIcons
{
    public static Geometry InformationSecondary { get; } = Parse(
        "M224,128a96,96,0,1,1-96-96A96,96,0,0,1,224,128Z");

    public static Geometry Information { get; } = Parse(
        "M144,176a8,8,0,0,1-8,8,16,16,0,0,1-16-16V128a8,8,0,0,1,0-16,16,16,0,0,1,16,16v40A8,8,0,0,1,144,176Zm88-48A104,104,0,1,1,128,24,104.11,104.11,0,0,1,232,128Zm-16,0a88,88,0,1,0-88,88A88.1,88.1,0,0,0,216,128ZM124,96a12,12,0,1,0-12-12A12,12,0,0,0,124,96Z");

    public static Geometry QuestionSecondary { get; } = Parse(
        "M224,128a96,96,0,1,1-96-96A96,96,0,0,1,224,128Z");

    public static Geometry Question { get; } = Parse(
        "M140,180a12,12,0,1,1-12-12A12,12,0,0,1,140,180ZM128,72c-22.06,0-40,16.15-40,36v4a8,8,0,0,0,16,0v-4c0-11,10.77-20,24-20s24,9,24,20-10.77,20-24,20a8,8,0,0,0-8,8v8a8,8,0,0,0,16,0v-.72c18.24-3.35,32-17.9,32-35.28C168,88.15,150.06,72,128,72Zm104,56A104,104,0,1,1,128,24,104.11,104.11,0,0,1,232,128Zm-16,0a88,88,0,1,0-88,88A88.1,88.1,0,0,0,216,128Z");

    private static Geometry Parse(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
