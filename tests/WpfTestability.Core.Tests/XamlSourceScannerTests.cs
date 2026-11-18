using WpfTestability.Core.Model;
using WpfTestability.Core.Source;

namespace WpfTestability.Core.Tests;

public class XamlSourceScannerTests
{
    private const string Head = """
        <Window x:Class="Demo.TestWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:Demo"
                xmlns:telerik="http://schemas.telerik.com/2008/xaml/presentation">
        """;

    private static ScanResult Scan(string body, string? csharp = null) =>
        new XamlSourceScanner().ScanText(Head + body + "</Window>", "TestWindow.xaml", csharp);

    private static UiElementInfo Single(ScanResult r, Func<UiElementInfo, bool> match) =>
        Assert.Single(r.Screens.Single().Elements, e => match(e));

    [Fact]
    public void Screen_is_named_after_x_Class()
    {
        var r = Scan("<Grid/>");
        Assert.Equal("TestWindow", r.Screens.Single().Name);
    }

    [Fact]
    public void Id_origin_follows_WPF_order_AutomationId_then_Uid_then_Name()
    {
        var r = Scan("""
            <StackPanel>
              <Button Content="A" AutomationProperties.AutomationId="ExplicitId" x:Uid="UidA" x:Name="NameA"/>
              <Button Content="B" x:Uid="HandWrittenUid" x:Name="NameB"/>
              <Button Content="C" x:Name="NameC"/>
              <Button Content="D"/>
            </StackPanel>
            """);

        var a = Single(r, e => e.Name == "A");
        Assert.Equal(("ExplicitId", IdOrigin.Explicit), (a.AutomationId, a.IdOrigin));
        var b = Single(r, e => e.Name == "B");
        Assert.Equal(("HandWrittenUid", IdOrigin.Uid), (b.AutomationId, b.IdOrigin));
        var c = Single(r, e => e.Name == "C");
        Assert.Equal(("NameC", IdOrigin.XName), (c.AutomationId, c.IdOrigin));
        var d = Single(r, e => e.Name == "D");
        Assert.Equal(IdOrigin.None, d.IdOrigin);
        Assert.Null(d.AutomationId);
    }

    [Fact]
    public void Generated_looking_and_data_bound_ids_are_recognised()
    {
        var r = Scan("""
            <StackPanel>
              <Button Content="Gen" x:Uid="Button_12"/>
              <Button Content="Guid" AutomationProperties.AutomationId="3f2504e0-4f89-11d3-9a0c-0305e82c3301"/>
              <Button Content="Bound" AutomationProperties.AutomationId="{Binding Id}"/>
            </StackPanel>
            """);

        Assert.Equal(IdOrigin.GeneratedLooking, Single(r, e => e.Name == "Gen").IdOrigin);
        Assert.Equal(IdOrigin.GeneratedLooking, Single(r, e => e.Name == "Guid").IdOrigin);
        Assert.Equal(IdOrigin.DataBound, Single(r, e => e.Name == "Bound").IdOrigin);
    }

    [Fact]
    public void Interactive_controls_are_counted_and_layout_is_not()
    {
        var r = Scan("""
            <Grid>
              <TextBlock Text="Hello"/>
              <Border><TextBox/></Border>
              <CheckBox Content="Ok"/>
              <ComboBox><ComboBoxItem>One</ComboBoxItem></ComboBox>
            </Grid>
            """);

        var interactive = r.Screens.Single().Elements.Where(e => e.IsInteractive).Select(e => e.Kind).OrderBy(k => k).ToList();
        Assert.Equal(["CheckBox", "ComboBox", "TextBox"], interactive);
        Assert.Contains(r.Screens.Single().Elements, e => e.Kind == "ComboBoxItem" && !e.IsInteractive);
    }

    [Fact]
    public void Styles_templates_and_resources_are_not_treated_as_screen_controls()
    {
        var r = Scan("""
            <Window.Resources>
              <Style TargetType="Button">
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Button"><Button Content="Inner"/></ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
            </Window.Resources>
            <Button Content="Real" AutomationProperties.AutomationId="RealButton"/>
            """);

        var buttons = r.Screens.Single().Elements.Where(e => e.Kind == "Button").ToList();
        Assert.Single(buttons);
        Assert.Equal("RealButton", buttons[0].AutomationId);
    }

    [Fact]
    public void Elements_in_item_templates_are_marked_repeated()
    {
        var r = Scan("""
            <ListBox x:Name="Orders">
              <ListBox.ItemTemplate>
                <DataTemplate><Button x:Name="Remove" Content="Remove"/></DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
            """);

        var remove = Single(r, e => e.AutomationId == "Remove");
        Assert.True(remove.InRepeatedTemplate);
        Assert.False(remove.RowsIdentifiable);
        var list = Single(r, e => e.Kind == "ListBox");
        Assert.True(list.IsItemsHost);
        Assert.False(list.RowsIdentifiable);
    }

    [Fact]
    public void Content_template_is_not_repeated()
    {
        var r = Scan("""
            <ContentControl>
              <ContentControl.ContentTemplate>
                <DataTemplate><Button Content="Once" AutomationProperties.AutomationId="OnceButton"/></DataTemplate>
              </ContentControl.ContentTemplate>
            </ContentControl>
            """);

        Assert.False(Single(r, e => e.AutomationId == "OnceButton").InRepeatedTemplate);
    }

    [Theory]
    [InlineData("""
        <ListBox ItemsSource="{Binding Items}">
          <ListBox.ItemContainerStyle>
            <Style TargetType="ListBoxItem"><Setter Property="AutomationProperties.AutomationId" Value="{Binding Id}"/></Style>
          </ListBox.ItemContainerStyle>
        </ListBox>
        """)]
    [InlineData("""
        <DataGrid ItemsSource="{Binding Items}" RowStyle="{StaticResource RowWithId}"/>
        """)]
    [InlineData("""
        <ListBox ItemsSource="{Binding Items}">
          <ListBox.ItemTemplate>
            <DataTemplate><StackPanel AutomationProperties.AutomationId="{Binding Id}"><TextBlock Text="{Binding Name}"/></StackPanel></DataTemplate>
          </ListBox.ItemTemplate>
        </ListBox>
        """)]
    [InlineData("""
        <ListView ItemsSource="{Binding Items}"/>
        """)]
    public void Rows_are_identifiable_when_each_row_gets_an_id(string list)
    {
        var r = Scan($$"""
            <Window.Resources>
              <Style x:Key="RowWithId" TargetType="DataGridRow"><Setter Property="AutomationProperties.AutomationId" Value="{Binding Id}"/></Style>
              <Style TargetType="ListViewItem"><Setter Property="AutomationProperties.AutomationId" Value="{Binding Id}"/></Style>
            </Window.Resources>
            {{list}}
            """);

        Assert.True(Single(r, e => e.IsItemsHost).RowsIdentifiable);
    }

    [Fact]
    public void Lists_with_hand_written_items_are_not_judged_on_rows()
    {
        var r = Scan("""<ListBox><ListBoxItem>A</ListBoxItem><ListBoxItem>B</ListBoxItem></ListBox>""");
        Assert.Null(Single(r, e => e.IsItemsHost).RowsIdentifiable);
    }

    [Fact]
    public void Names_come_from_content_labels_and_automation_properties()
    {
        var r = Scan("""
            <StackPanel>
              <Button Content="_Save" AutomationProperties.AutomationId="B1"/>
              <Button AutomationProperties.AutomationId="B2"><TextBlock Text="Nested"/></Button>
              <Label Content="_City:" Target="{Binding ElementName=CityBox}"/>
              <TextBox x:Name="CityBox"/>
              <TextBlock x:Name="ZipLabel" Text="Zip"/>
              <TextBox AutomationProperties.AutomationId="ZipBox" AutomationProperties.LabeledBy="{Binding ElementName=ZipLabel}"/>
              <TextBox AutomationProperties.AutomationId="Unlabelled" Text="typed text is not a name"/>
              <Button AutomationProperties.AutomationId="IconOnly" ToolTip="Refresh"><Path Data="M0,0 L1,1"/></Button>
            </StackPanel>
            """);

        Assert.Equal("Save", Single(r, e => e.AutomationId == "B1").Name);
        Assert.Equal("Nested", Single(r, e => e.AutomationId == "B2").Name);
        Assert.Equal("City", Single(r, e => e.AutomationId == "CityBox").Name);
        Assert.Equal("Zip", Single(r, e => e.AutomationId == "ZipBox").Name);
        Assert.False(Single(r, e => e.AutomationId == "Unlabelled").HasName);
        var icon = Single(r, e => e.AutomationId == "IconOnly");
        Assert.False(icon.HasName);
        Assert.Equal("Refresh", icon.Hint);
    }

    [Fact]
    public void Custom_control_without_peer_is_invisible_and_counted_as_interactive()
    {
        const string code = """
            using System.Windows.Controls;
            namespace Demo;
            public class Rating : Control { }
            public class GoodRating : Rating { protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => null!; }
            public class Swatches : WrapPanel { }
            """;
        var r = Scan("""
            <StackPanel>
              <local:Rating AutomationProperties.AutomationId="R1"/>
              <local:GoodRating AutomationProperties.AutomationId="R2"/>
              <local:Swatches AutomationProperties.AutomationId="S1"/>
              <local:Swatches/>
            </StackPanel>
            """, code);

        var bad = Single(r, e => e.AutomationId == "R1");
        Assert.False(bad.VisibleToAutomation);
        Assert.True(bad.IsInteractive);

        var good = Single(r, e => e.AutomationId == "R2");
        Assert.True(good.VisibleToAutomation);
        Assert.True(good.IsInteractive);

        var panel = Single(r, e => e.AutomationId == "S1");
        Assert.False(panel.VisibleToAutomation);
        Assert.False(panel.IsInteractive);

        Assert.Equal(1, r.CustomControlUsage["Rating"]);
        Assert.Equal(2, r.CustomControlUsage["Swatches"]);
    }

    [Theory]
    [InlineData("RadComboBox", "ComboBox")]
    [InlineData("RadRadioButton", "RadioButton")]
    [InlineData("RadGridView", "DataGrid")]
    [InlineData("GridControl", "DataGrid")]
    [InlineData("XamDataGrid", "DataGrid")]
    [InlineData("FancyChart", null)]
    public void Third_party_controls_are_guessed_from_their_names(string className, string? expected) =>
        Assert.Equal(expected, XamlSourceScanner.GuessThirdPartyType(className));

    [Fact]
    public void Third_party_controls_count_as_interactive()
    {
        var r = Scan("""<telerik:RadButton Content="Go"/>""");
        var e = Single(r, x => x.Kind == "telerik:RadButton");
        Assert.True(e.IsInteractive);
        Assert.Equal("Button", e.ControlType);
    }

    [Fact]
    public void Source_locations_point_at_the_right_line()
    {
        var r = Scan("""

            <Button Content="Here"/>
            """);
        var b = Single(r, e => e.Kind == "Button");
        Assert.Equal("TestWindow.xaml", b.Location!.File);
        Assert.Equal(6, b.Location.Line);
    }

    [Fact]
    public void Resource_dictionaries_and_app_files_are_not_screens()
    {
        var r = new XamlSourceScanner().ScanText("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"/>
            """);
        Assert.Empty(r.Screens);
    }
}
