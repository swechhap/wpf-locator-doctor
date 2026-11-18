using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Tests;

public class CodeBehindAnalyzerTests
{
    [Fact]
    public void Classifies_custom_controls_by_whether_test_tools_can_see_them()
    {
        var result = CodeBehindAnalyzer.AnalyzeText("""
            using System.Windows;
            using System.Windows.Controls;
            using System.Windows.Controls.Primitives;
            using System.Windows.Automation.Peers;

            namespace Demo;

            public class NoPeer : Control { }
            public class WithPeer : Control
            {
                protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
            }
            public class InheritsFix : WithPeer { }
            public class InheritsProblem : NoPeer, IDisposable { public void Dispose() { } }
            public class MyButtonBase : ButtonBase { }
            public class SearchBox : TextBox { }
            public partial class Shell : Window { }
            public partial class AddressView : UserControl { }
            public class Swatches : System.Windows.Controls.WrapPanel { }
            public class OrdersViewModel : INotifyPropertyChanged { }
            public class ThirdParty : Telerik.Windows.Controls.RadButton { }
            """);

        Assert.Equal(PeerStatus.Invisible, result["NoPeer"].Status);
        Assert.Equal(PeerStatus.Visible, result["WithPeer"].Status);
        Assert.Equal(PeerStatus.Visible, result["InheritsFix"].Status);
        Assert.Equal(PeerStatus.Invisible, result["InheritsProblem"].Status);
        Assert.Equal("Control", result["InheritsProblem"].WpfBase);
        Assert.Equal(PeerStatus.Invisible, result["MyButtonBase"].Status);
        Assert.Equal(PeerStatus.Visible, result["SearchBox"].Status);
        Assert.Equal("TextBox", result["SearchBox"].WpfBase);
        Assert.Equal(PeerStatus.Visible, result["Shell"].Status);
        Assert.Equal(PeerStatus.Visible, result["AddressView"].Status);
        Assert.Equal(PeerStatus.NoPeerLayout, result["Swatches"].Status);

        Assert.False(result.ContainsKey("OrdersViewModel"));
        Assert.False(result.ContainsKey("ThirdParty"));
    }

    [Fact]
    public void Partial_classes_combine_their_parts()
    {
        var result = CodeBehindAnalyzer.AnalyzeText("""
            namespace Demo;
            public partial class Gauge : System.Windows.Controls.Control { }
            public partial class Gauge
            {
                protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => null!;
            }
            """);

        Assert.Equal(PeerStatus.Visible, result["Gauge"].Status);
    }

    [Fact]
    public void Records_where_the_class_is_declared()
    {
        var result = CodeBehindAnalyzer.AnalyzeText("""
            namespace Demo;

            public class Dial : System.Windows.Controls.Control { }
            """, "Controls/Dial.cs");

        Assert.Equal(new SourceLocation("Controls/Dial.cs", 3, 14), result["Dial"].Location);
    }
}
