using System.Windows;
using System.Windows.Controls;
using Tooltail.Domain.Agents;

namespace Tooltail.Desktop.Controls;

public partial class AgentBodyControl : UserControl
{
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body),
        typeof(CompanionBodyProjection),
        typeof(AgentBodyControl),
        new FrameworkPropertyMetadata(default(CompanionBodyProjection)));

    public static readonly DependencyProperty AccessibleNameProperty =
        DependencyProperty.Register(
            nameof(AccessibleName),
            typeof(string),
            typeof(AgentBodyControl),
            new FrameworkPropertyMetadata("Tooltail body state"));

    public static readonly DependencyProperty ReducedMotionProperty =
        DependencyProperty.Register(
            nameof(ReducedMotion),
            typeof(bool),
            typeof(AgentBodyControl),
            new FrameworkPropertyMetadata(false));

    public AgentBodyControl() => InitializeComponent();

    public CompanionBodyProjection? Body
    {
        get => (CompanionBodyProjection?)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public string AccessibleName
    {
        get => (string)GetValue(AccessibleNameProperty);
        set => SetValue(AccessibleNameProperty, value);
    }

    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }
}
