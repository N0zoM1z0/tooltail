using System.Windows;
using System.Windows.Controls;
using Tooltail.Features.FileSkills.Presentation;

namespace Tooltail.Desktop.Controls;

public partial class SkillCardControl : UserControl
{
    public static readonly DependencyProperty CardProperty = DependencyProperty.Register(
        nameof(Card),
        typeof(SkillCardViewModel),
        typeof(SkillCardControl),
        new FrameworkPropertyMetadata(default(SkillCardViewModel)));

    public static readonly RoutedEvent ActionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(ActionRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(SkillCardControl));

    public SkillCardControl() => InitializeComponent();

    public SkillCardViewModel? Card
    {
        get => (SkillCardViewModel?)GetValue(CardProperty);
        set => SetValue(CardProperty, value);
    }

    public event RoutedEventHandler ActionRequested
    {
        add => AddHandler(ActionRequestedEvent, value);
        remove => RemoveHandler(ActionRequestedEvent, value);
    }

    private void OnActionClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button
            {
                DataContext: SkillCardActionViewModel { IsEnabled: true } action,
            })
        {
            RaiseEvent(new SkillCardActionRequestedEventArgs(
                ActionRequestedEvent,
                this,
                action.Code));
        }

        eventArgs.Handled = true;
    }
}

public sealed class SkillCardActionRequestedEventArgs : RoutedEventArgs
{
    public SkillCardActionRequestedEventArgs(
        RoutedEvent routedEvent,
        object source,
        SkillCardActionCode action)
        : base(routedEvent, source) => Action = action;

    public SkillCardActionCode Action { get; }
}
