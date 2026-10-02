using System.Xml.Linq;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §حارس تخطيط (B51): يمنع عودة عطل «الأعمدة الجانبية».
///
/// سبب وجوده: في WPF أي طفل داخل DockPanel بلا DockPanel.Dock صريحة يُعامَل افتراضياً
/// كعمود جانبي (Left). هذا حوّل شرائح مسار المعاملة في شاشة الخطط إلى أعمدة رأسية فارغة،
/// ودفع لوحة تبويبات الأصناف إلى الحافة — كما ظهر في لقطات المستخدم 2026-09-02.
///
/// القاعدة المفحوصة: في كل DockPanel جذر (LastChildFill) — كل طفل ظاهر عدا الأخير له
/// DockPanel.Dock صريح، والأخير وحده بلا Dock (فهو يملأ الوسط).
/// </summary>
public class LayoutStructureTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static XElement RootDockPanel(XDocument doc, string preferName = null)
    {
        XName dock = XName.Get("DockPanel", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        XName nameAttr = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        var panels = doc.Descendants(dock)
            .Where(d => (string)d.Attribute("LastChildFill") == "True").ToList();
        var named = panels.Where(p => (string)p.Attribute(nameAttr) == preferName).ToList();
        var anon = panels.Where(p => (string)p.Attribute(nameAttr) == null).ToList();
        var pick = preferName != null && named.Count > 0 ? named : anon.Count > 0 ? anon : panels;
        Assert.NotEmpty(pick);
        return pick[0];
    }

    [Theory]
    [InlineData("ReceivingView.xaml", null)]
    [InlineData("OrdersView.xaml", "ListArea")]
    [InlineData("QualityView.xaml", null)]
    [InlineData("DeliveryView.xaml", null)]
    [InlineData("FinishedGoodsView.xaml", null)]
    public void RootDockPanel_NoAccidentalSideColumns(string file, string? panelName)
    {
        // الخاصية المرفقة تُكتب في XML باسمها الحرفي «DockPanel.Dock» بلا namespace
        XName dockAttr = XName.Get("DockPanel.Dock");
        var path = Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Views/Screens", file);
        var dp = RootDockPanel(XDocument.Load(path), panelName);
        var kids = dp.Elements().ToList();
        Assert.True(kids.Count >= 2, $"{file}: الجذر فقير بالأطفال ({kids.Count})");

        for (int i = 0; i < kids.Count - 1; i++)
        {
            bool docked = kids[i].Attribute(dockAttr) != null;
            bool collapsed = (string)kids[i].Attribute("Visibility") == "Collapsed";
            Assert.True(docked || collapsed,
                $"{file}: الطفل #{i + 1} ({kids[i].Name.LocalName}) بلا DockPanel.Dock وليس الأخير — " +
                "سيصيره WPF عموداً جانبياً (عطل B51). أعطِه Dock صريحاً أو اجعله الأخير.");
        }

        var last = kids[kids.Count - 1];
        Assert.True(last.Attribute(dockAttr) == null && (string)last.Attribute("Visibility") != "Collapsed",
            $"{file}: الطفل الأخير يجب أن يبقى بلا Dock ليملأ وسط النافذة.");
    }
}
