using System.Globalization;

namespace Roost.Desktop.Services;

/// <summary>
/// La cultura con la que la app escribe números y fechas («1.234,5 MB», «22-oct, 18:05»). Va hecha a
/// mano sobre la invariante, sin pedirle <c>es-ES</c> a ICU: la app corre con
/// <c>InvariantGlobalization</c> (como la CLI), y así en Linux no exige el <c>libicu</c> de cada distro
/// y en los dos sistemas sale exactamente lo mismo. Nada de la app debe llamar a
/// <c>CultureInfo.GetCultureInfo</c> con una cultura concreta: en modo invariante no existe ninguna.
/// </summary>
public static class Es
{
    public static readonly CultureInfo Culture = Build();

    private static CultureInfo Build()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        var numbers = culture.NumberFormat;
        numbers.NumberDecimalSeparator = ",";
        numbers.NumberGroupSeparator = ".";
        numbers.NumberGroupSizes = [3];
        numbers.PercentDecimalSeparator = ",";
        numbers.PercentGroupSeparator = ".";
        numbers.CurrencyDecimalSeparator = ",";
        numbers.CurrencyGroupSeparator = ".";

        var dates = culture.DateTimeFormat;
        dates.MonthNames = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre", ""];
        dates.MonthGenitiveNames = dates.MonthNames;
        dates.AbbreviatedMonthNames = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic", ""];
        dates.AbbreviatedMonthGenitiveNames = dates.AbbreviatedMonthNames;
        dates.DayNames = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];
        dates.AbbreviatedDayNames = ["dom", "lun", "mar", "mié", "jue", "vie", "sáb"];
        dates.ShortestDayNames = ["D", "L", "M", "X", "J", "V", "S"];
        dates.FirstDayOfWeek = DayOfWeek.Monday;
        dates.ShortDatePattern = "dd/MM/yyyy";
        dates.LongDatePattern = "d 'de' MMMM 'de' yyyy";
        dates.ShortTimePattern = "H:mm";
        dates.LongTimePattern = "H:mm:ss";
        dates.AMDesignator = "";
        dates.PMDesignator = "";
        return CultureInfo.ReadOnly(culture);
    }
}
