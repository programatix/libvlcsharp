using LibVLCSharp.Shared;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Linq;
using static LibVLCSharp.WPF.User32Wrapper;

namespace LibVLCSharp.WPF
{
    internal class ForegroundWindow : Window
    {
        private Window? _wndhost;
        private IntPtr _hWnd;
        private readonly FrameworkElement _bckgnd;
        private readonly Point _zeroPoint = new Point(0, 0);
        private readonly Grid _grid = new Grid();

        private UIElement? _overlayContent;

        internal UIElement? OverlayContent
        {
            get => _overlayContent;
            set
            {
                _overlayContent = value;
                _grid.Children.Clear();
                if (_overlayContent != null)
                {
                    _grid.Children.Add(_overlayContent);
                }
            }
        }

        internal ForegroundWindow(FrameworkElement background)
        {
            Title = "LibVLCSharp.WPF";
            Height = 300;
            Width = 300;
            WindowStyle = WindowStyle.None;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            ShowInTaskbar = false;
            Content = _grid;

            DataContext = background.DataContext;

            _bckgnd = background;
            _bckgnd.DataContextChanged += Background_DataContextChanged;
            _bckgnd.Loaded += Background_Loaded;
            _bckgnd.Unloaded += Background_Unloaded;
        }

        private void Background_DataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            DataContext = e.NewValue;
        }

        private void Background_Unloaded(object? sender, RoutedEventArgs e)
        {
            _bckgnd.SizeChanged -= Bckgnd_SizeChanged;
            _bckgnd.LayoutUpdated -= Bckgnd_LayoutUpdated;
            if (_wndhost != null)
            {
                /*_wndhost.Closing -= Wndhost_Closing;
                _wndhost.LocationChanged -= Wndhost_LocationChanged;*/
            }

            Hide();
        }

        private DependencyObject GetRoot(DependencyObject obj)
        {
            var parent = VisualTreeHelper.GetParent(obj);
            if (parent != null)
            {
                return GetRoot(parent);
            }
            else
            {
                return obj;
            }
        }

        private void Background_Loaded(object? sender, RoutedEventArgs e)
        {
            if (_wndhost != null && IsVisible)
            {
                return;
            }

            _wndhost = GetWindow(_bckgnd);
            //Trace.Assert(_wndhost != null, $"{nameof(_wndhost)} is null");
            if (_wndhost != null)
            {
                Owner = _wndhost;
            }
            else
            {
                using var p = Process.GetCurrentProcess();
                _hWnd = p.MainWindowHandle;
                var helper = new WindowInteropHelper(this)
                {
                    Owner = _hWnd
                };
            }

            /*_wndhost.Closing += Wndhost_Closing;
            _wndhost.LocationChanged += Wndhost_LocationChanged;*/
            _bckgnd.LayoutUpdated += Bckgnd_LayoutUpdated;
            _bckgnd.SizeChanged += Bckgnd_SizeChanged;

            try
            {
                AlignWithBackground();
                Show();
                _wndhost?.Focus();
            }
            catch (Exception ex)
            {
                Hide();
                throw new VLCException("Unable to create WPF Window in VideoView.", ex);
            }
        }

        private void Bckgnd_LayoutUpdated(object? sender, EventArgs e)
        {
            AlignWithBackground();
        }

        private void Wndhost_LocationChanged(object? sender, EventArgs e)
        {
            AlignWithBackground();
        }

        private void Bckgnd_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            AlignWithBackground();
        }

        private void AlignWithBackground()
        {
            PresentationSource source;
            if (_wndhost == null)
            {
                var rct = new RECT();
                GetWindowRect(_hWnd, ref rct);
                source = new HwndSource(0, 0, 0, rct.Left, rct.Top, rct.Right - rct.Left, rct.Bottom - rct.Top, "a", IntPtr.Zero);
            }
            else
            {
                source = PresentationSource.FromVisual(_wndhost);
            }

            if (source == null)
            {
                return;
            }

            if (PresentationSource.FromVisual(_bckgnd) == null)
            {
                return;
            }

            if (double.IsNaN(_bckgnd.ActualWidth) || double.IsNaN(_bckgnd.ActualHeight) ||
                _bckgnd.ActualWidth == 0 || _bckgnd.ActualHeight == 0)
            {
                return;
            }

            /*
             * Note that _bckgnd.ActualWidth and _bckgnd.ActualWidth are in local coordinates
             * and not in screen coordinates.
             *
             * Sizes in local and screen coordinates differ when a scale transformation has
             * been applied to _bckgnd or one of its ancestors up to the parent window.
             * This is also the case when the video or one of its ancestors is contained
             * in a Viewbox.
             *
             * To correctly position and size the ForegroundWindow, we transform the extent
             * of _bckgnd (top-left and bottom-right points) from _bckgnd coordinates
             * to screen coordinates and use them as the extents of ForegroundWindow.
             *
             * In case a scaling is detected, we should also apply that scaling to the
             * content of ForegroundWindow, so it naturally scales up and down with the
             * rest of the parent controls.
             *
             * The video view itself natively supports scaling.
             */

            var startLocationFromScreen = _bckgnd.PointToScreen(_zeroPoint);
            var startLocationPoint = source.CompositionTarget.TransformFromDevice.Transform(startLocationFromScreen);
            var endLocationFromScreen = _bckgnd.PointToScreen(new Point(_bckgnd.ActualWidth, _bckgnd.ActualHeight));
            var endLocationPoint = source.CompositionTarget.TransformFromDevice.Transform(endLocationFromScreen);

            Left = Math.Min(startLocationPoint.X, endLocationPoint.X);
            Top = Math.Min(startLocationPoint.Y, endLocationPoint.Y);
            Width = Math.Abs(endLocationPoint.X - startLocationPoint.X);
            Height = Math.Abs(endLocationPoint.Y - startLocationPoint.Y);

            if (Math.Abs(Width - _bckgnd.ActualWidth) + Math.Abs(Height - _bckgnd.ActualHeight) > 0.5)
            {
                ScaleWindowContent(Width / _bckgnd.ActualWidth, Height / _bckgnd.ActualHeight);
            }
        }

        private void ScaleWindowContent(double scaleX, double scaleY)
        {
            if (VisualChildrenCount <= 0)
            {
                return;
            }

            var child = (FrameworkElement)GetVisualChild(0)!;

            // Do not re-create and apply the ScaleTransform if already scaled
            // That would lead to an infinite layout update cycle
            if (child.LayoutTransform is ScaleTransform scaleTransform &&
                Math.Abs(scaleTransform.ScaleX - scaleX) < 0.01 &&
                Math.Abs(scaleTransform.ScaleY - scaleY) < 0.01)
            {
                return;
            }

            child.LayoutTransform = new ScaleTransform(scaleX, scaleY);
        }

        private void Wndhost_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (e.Cancel)
            {
                return;
            }

            Close();

            _bckgnd.DataContextChanged -= Background_DataContextChanged;
            _bckgnd.Loaded -= Background_Loaded;
            _bckgnd.Unloaded -= Background_Unloaded;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.System && e.SystemKey == Key.F4)
            {
                _wndhost?.Focus();
            }
        }
    }
}
