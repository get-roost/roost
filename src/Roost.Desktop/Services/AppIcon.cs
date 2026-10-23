using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Roost.Desktop.Services;

/// <summary>
/// El icono de Roost (la casita blanca sobre el cuadrado azul del mockup), dibujado en código:
/// el de la ventana y el de la bandeja, que lleva un punto con el estado del stack.
/// </summary>
public static class AppIcon
{
    private static readonly Geometry Mark = StreamGeometry.Parse("M4 12 L12 5 L20 12 M7 11 V19 H17 V11 M10 19 V15 H14 V19");

    public static WindowIcon Window() => new(Render(64, null));

    /// <summary>Icono de bandeja; <paramref name="dot"/> es el color del punto de estado (null, sin punto).</summary>
    public static WindowIcon Tray(Color? dot) => new(Render(32, dot));

    public static Bitmap Render(int size, Color? dot)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#2E57CC")), null, new RoundedRect(new Rect(0, 0, size, size), size * 0.26));
            var scale = size / 24.0 * 0.78;
            var offset = (size - 24 * scale) / 2;
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset, offset)))
                context.DrawGeometry(null, new Pen(Brushes.White, 2.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Mark);

            if (dot is { } color)
            {
                var radius = size * 0.2;
                var center = new Point(size - radius - 1, size - radius - 1);
                context.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, size * 0.07), center, radius, radius);
            }
        }
        return bitmap;
    }
}
