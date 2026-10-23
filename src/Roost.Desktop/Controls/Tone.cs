using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Roost.Desktop.Controls;

/// <summary>El semáforo del mockup: verde, ámbar, rojo, neutro, acento y apagado.</summary>
public enum Tone
{
    Neutral,
    Ok,
    Warn,
    Bad,
    Accent,
    Off,
}

/// <summary>La pastilla de estado (chip): texto corto, con punto opcional, coloreado por <see cref="Tone"/>.</summary>
public sealed class Chip : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Chip, string?>(nameof(Text));

    public static readonly StyledProperty<Tone> ToneProperty =
        AvaloniaProperty.Register<Chip, Tone>(nameof(Tone));

    public static readonly StyledProperty<bool> ShowDotProperty =
        AvaloniaProperty.Register<Chip, bool>(nameof(ShowDot));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Tone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public bool ShowDot { get => GetValue(ShowDotProperty); set => SetValue(ShowDotProperty, value); }
}

/// <summary>El punto de estado de 8 px.</summary>
public sealed class Dot : Control
{
    public static readonly StyledProperty<Tone> ToneProperty =
        AvaloniaProperty.Register<Dot, Tone>(nameof(Tone));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Dot, IBrush?>(nameof(Fill));

    static Dot()
    {
        AffectsRender<Dot>(FillProperty);
    }

    public Tone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(8, 8);

    public override void Render(DrawingContext context)
    {
        if (Fill is { } fill)
            context.DrawEllipse(fill, null, new Rect(Bounds.Size).Center, 4, 4);
    }
}

/// <summary>Aviso en línea (la «note» del mockup): icono según el tono y el contenido al lado.</summary>
public sealed class Note : ContentControl
{
    public static readonly StyledProperty<Tone> ToneProperty =
        AvaloniaProperty.Register<Note, Tone>(nameof(Tone), Tone.Accent);

    public Tone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
}

/// <summary>Para encender clases por tono desde XAML (Classes.bad="{Binding Tono, Converter=...}").</summary>
public static class ToneConverters
{
    public static readonly IValueConverter IsOk = new FuncValueConverter<Tone, bool>(t => t == Tone.Ok);
    public static readonly IValueConverter IsWarn = new FuncValueConverter<Tone, bool>(t => t == Tone.Warn);
    public static readonly IValueConverter IsBad = new FuncValueConverter<Tone, bool>(t => t == Tone.Bad);

    /// <summary>true → seminegrita (el paso en curso de una instalación).</summary>
    public static readonly IValueConverter Bold = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);
}

/// <summary>Conversores sueltos para XAML.</summary>
public static class ValueConverters
{
    /// <summary>Cualquier valor a texto (para pasar un número como parámetro de un comando).</summary>
    public static readonly IValueConverter ToText = new FuncValueConverter<object?, string>(v => v?.ToString() ?? "");

    /// <summary>El texto de una lista vacía de la auditoría, según esté escaneando o no.</summary>
    public static readonly IValueConverter ScanningText = new FuncValueConverter<bool, string>(scanning =>
        scanning ? "Escaneando el equipo: PHP, MySQL y MariaDB de Roost, Laragon, XAMPP, el PATH…" : "No hay nada que enseñar con este filtro.");
}
