using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitFlexConduit;

public sealed class App : IExternalApplication
{
    internal const string TabName = "Electrical Tools";
    internal const string PanelName = "Flex Conduit";

    private const string Icon32Base64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAA9ElEQVR4nO2Wyw3DIAyGcdWR2nt2YIGMxgLZIfeyEz1BE2I7GFtBqvLfAtj+/CDCuVtKpdUljf3DCmQIgDZ7NYCFhgM8ew3Pyv+al7IfgwfqnEkFaphtcOzbHKA1mBkAVX4uOLVH9qY22vYRA3iHhXN1UPZHAmDE2aj1/p9BxeABBeBK+Zl9S2wH0y85yl8MHi75D3DX8LAhnWKsIjDhrcVmqulHFIMHKRjlp17bLXCDh52RZE+pzEBL8LxG9VQafAcgFTdYYoDW7DlBR/YFoEcWjxEVwFbQmX0BqMstKb8muEppdcmqDUODD38TdmlY6W/9pb5e8HLetjA3HgAAAABJRU5ErkJggg==";
    private const string Icon16Base64 = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAgklEQVR4nL1Syw2AIAx9JY6Ed3dgAUZzAXbgrjvVgykh5eOH6LuQtO+XFOBrcAT39mY0oGug060PRZvbDUSsTQqDWoqeWR9YZtQibt5Vm9ACynmpQS25JgaAfXXpnTRJljNOw1YT4VGeLkNBfgVJ1zBPqjfRMrn6hV1wBA8bvBb/igPTjT8qNv3lxAAAAABJRU5ErkJggg==";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            try { application.CreateRibbonTab(TabName); } catch { }
            var panel = application.GetRibbonPanels(TabName).FirstOrDefault(p => string.Equals(p.Name, PanelName, StringComparison.OrdinalIgnoreCase)) ?? application.CreateRibbonPanel(TabName, PanelName);
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            var buttonData = new PushButtonData("RevitFlexConduit.Create", "Flex\nConduit", assemblyPath, typeof(FlexConduitCommand).FullName!);
            buttonData.ToolTip = "Draw a smooth electrical flex-conduit run using native Revit conduit segments. Pick two or more points; press ESC to finish.";
            buttonData.LongDescription = "Creates a connected electrical conduit path that follows a smoothed curve through the points you pick. If a conduit is preselected, its type, diameter and selected instance parameters are inherited.";
            buttonData.LargeImage = LoadImage(Icon32Base64);
            buttonData.Image = LoadImage(Icon16Base64);
            var item = panel.AddItem(buttonData) as PushButton;
            if (item != null) item.AvailabilityClassName = typeof(FlexConduitAvailability).FullName;
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Flex Conduit", "The ribbon panel could not be created.\n\n" + ex.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static BitmapImage LoadImage(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
}

public sealed class FlexConduitAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, Autodesk.Revit.DB.CategorySet selectedCategories) => applicationData.ActiveUIDocument?.Document != null;
}
