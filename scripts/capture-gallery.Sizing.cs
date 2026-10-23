using CommandDeck;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

sealed partial class GalleryCapture
{

    private static bool IsSizingOption( string argument )
    {
        return argument is GalleryCaptureOptions.Width or GalleryCaptureOptions.Height or GalleryCaptureOptions.Dpr or
            GalleryCaptureOptions.DisplayPercent or GalleryCaptureOptions.FontScale or GalleryCaptureOptions.PanelWidth or GalleryCaptureOptions.PanelHeight;
    }

    private bool TrySetSizing( string key, string text )
    {
        if ( !TryReadPositiveSize( text, out var value ) )
        {
            return false;
        }

        var valid = key switch
        {
            GalleryCaptureOptions.Width or GalleryCaptureOptions.Height => value <= MaximumDimensionPixels && value == Math.Round( value ),
            GalleryCaptureOptions.Dpr => value <= 3,
            GalleryCaptureOptions.FontScale => value <= 2,
            GalleryCaptureOptions.PanelWidth or GalleryCaptureOptions.PanelHeight => IsValidPanelPercent( value ),
            GalleryCaptureOptions.DisplayPercent => IsValidDisplayPercent( value ),
            _ => false
        };
        if ( valid )
        {
            _sizing[key[2..]] = value;
        }

        return valid;
    }

    private static bool IsValidPanelPercent( double value )
    {
        return value is >= PanelLayoutLimits.MinimumPercent and <= PanelLayoutLimits.MaximumPercent && value % PanelLayoutLimits.StepPercent == 0;
    }

    private static bool IsValidDisplayPercent( double value )
    {
        return value is >= DisplaySizeLimits.MinimumPercent and <= DisplaySizeLimits.MaximumPercent && value % DisplaySizeLimits.StepPercent == 0;
    }

    private static bool HasOutputArgument( string[] arguments, int index )
    {
        return arguments[index] == OutputOption && index + 1 < arguments.Length;
    }
    private bool TrySetNextSizing( string key, string[] arguments, ref int index )
    {
        return ++index < arguments.Length && TrySetSizing( key, arguments[index] );
    }

    private static bool TryReadPositiveSize( string text, out double value )
    {
        return double.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out value ) && double.IsFinite( value ) && value > 0;
    }
}
