using System.Collections.Specialized;
using System.Windows.Controls;

namespace Pusher.App.Views;

public partial class LogsView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public LogsView()
    {
        InitializeComponent();

        // Auto-scroll to the newest line whenever the filtered list changes.
        DataContextChanged += (_, _) =>
        {
            if (_observed is not null) return; // Lines collection instance never changes per VM
            if (LinesList.ItemsSource is INotifyCollectionChanged c)
            {
                _observed = c;
                c.CollectionChanged += (_, _) => ScrollToEnd();
                ScrollToEnd();
            }
        };
    }

    private void ScrollToEnd()
    {
        if (LinesList.Items.Count > 0)
            LinesList.ScrollIntoView(LinesList.Items[LinesList.Items.Count - 1]);
    }
}
