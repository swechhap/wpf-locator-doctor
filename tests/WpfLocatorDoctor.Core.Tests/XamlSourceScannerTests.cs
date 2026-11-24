using WpfLocatorDoctor.Core.Model;
using WpfLocatorDoctor.Core.Rules;
using WpfLocatorDoctor.Core.Source;

namespace WpfLocatorDoctor.Core.Tests;

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
    public void A_hyperlink_uses_x_Name_but_not_x_Uid_and_text_parts_are_not_listed()
    {
        // WPF's FrameworkContentElementAutomationPeer skips x:Uid (also seen live on WPF-Samples' GlobalizationHomepage) but uses x:Name.
        var r = Scan("""
            <TextBlock>
              <Hyperlink x:Uid="Hyperlink_1" NavigateUri="https://example.com">Uid link</Hyperlink>
              <Hyperlink x:Uid="Hyperlink_3" x:Name="NamedLink">Named link</Hyperlink>
              <Hyperlink AutomationProperties.AutomationId="HelpLink" x:Uid="Hyperlink_2">Help</Hyperlink>
              <Run x:Name="StatusRun" Text="Ready"/>
            </TextBlock>
            """);

        var uidLink = Single(r, e => e.Name == "Uid link");
        Assert.Equal((null, IdOrigin.None), (uidLink.AutomationId, uidLink.IdOrigin));
        Assert.True(uidLink.IsInteractive);

        var namedLink = Single(r, e => e.Name == "Named link");
        Assert.Equal(("NamedLink", IdOrigin.XName), (namedLink.AutomationId, namedLink.IdOrigin));

        var help = Single(r, e => e.Name == "Help");
        Assert.Equal(("HelpLink", IdOrigin.Explicit), (help.AutomationId, help.IdOrigin));

        // UI Automation never shows a Run as an element of its own.
        Assert.DoesNotContain(r.Screens.Single().Elements, e => e.Kind == "Run");
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
    public void Label_Target_can_be_a_plain_element_name()
    {
        var r = Scan("""
            <StackPanel>
              <Label Content="_Id:" Target="IdBox"/>
              <TextBox x:Name="IdBox"/>
              <Label Content="_Version:" Target="{x:Reference VersionBox}"/>
              <TextBox x:Name="VersionBox"/>
              <Label Content="Not a name" Target="{Binding SomeProperty}"/>
              <TextBox x:Name="OtherBox"/>
            </StackPanel>
            """);

        Assert.Equal("Id", Single(r, e => e.XName == "IdBox").Name);
        Assert.Equal("Version", Single(r, e => e.XName == "VersionBox").Name);
        Assert.False(Single(r, e => e.XName == "OtherBox").HasName);
    }

    [Fact]
    public void Menu_item_without_a_header_is_named_by_a_built_in_command()
    {
        var r = Scan("""
            <Menu>
              <MenuItem AutomationProperties.AutomationId="Copy" Command="ApplicationCommands.Copy"/>
              <MenuItem AutomationProperties.AutomationId="Undo" Command="Undo"/>
              <MenuItem AutomationProperties.AutomationId="SelectAll" Command="{x:Static ApplicationCommands.SelectAll}"/>
              <MenuItem AutomationProperties.AutomationId="Back" Command="NavigationCommands.BrowseBack"/>
              <MenuItem AutomationProperties.AutomationId="Header" Header="_Paste here" Command="Paste"/>
              <MenuItem AutomationProperties.AutomationId="Mine" Command="local:AppCommands.Export"/>
              <MenuItem AutomationProperties.AutomationId="Bound" Command="{Binding ExportCommand}"/>
            </Menu>
            """);

        Assert.Equal("Copy", Single(r, e => e.AutomationId == "Copy").Name);
        Assert.Equal("Undo", Single(r, e => e.AutomationId == "Undo").Name);
        Assert.Equal("Select All", Single(r, e => e.AutomationId == "SelectAll").Name);
        Assert.Equal("Back", Single(r, e => e.AutomationId == "Back").Name);
        Assert.Equal("Paste here", Single(r, e => e.AutomationId == "Header").Name);
        Assert.False(Single(r, e => e.AutomationId == "Mine").HasName);
        Assert.False(Single(r, e => e.AutomationId == "Bound").HasName);
    }

    [Fact]
    public void A_button_does_not_take_its_name_from_a_command()
    {
        var r = Scan("""<Button AutomationProperties.AutomationId="CopyButton" Command="ApplicationCommands.Copy"/>""");
        Assert.False(Single(r, e => e.AutomationId == "CopyButton").HasName);
    }

    [Theory]
    [InlineData("Close", "Close")]
    [InlineData("ApplicationCommands.Close", "Close")]
    [InlineData("{x:Static Member=MediaCommands.TogglePlayPause}", "Toggle Play Pause")]
    [InlineData("{x:Static local:Commands.Close}", null)]
    [InlineData("{x:Static Close}", null)]
    [InlineData("NavigationCommands.BrowseHome", "Home")]
    [InlineData("SelectToPageUp", "Select To PageUp")]
    [InlineData("EditingCommands.ToggleBold", "ToggleBold")]
    [InlineData("NoSuchCommand", null)]
    [InlineData("ApplicationCommands.NoSuchCommand", null)]
    [InlineData("hc:ControlCommands.Close", null)]
    [InlineData("{Binding CloseCommand}", null)]
    [InlineData("", null)]
    public void Built_in_command_text_is_recognised(string command, string? expected) =>
        Assert.Equal(expected, BuiltInCommands.TextOf(command));

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
