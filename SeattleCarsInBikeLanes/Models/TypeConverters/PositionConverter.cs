using Microsoft.Azure.Cosmos.Spatial;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SeattleCarsInBikeLanes.Models.TypeConverters
{
    internal sealed class PositionConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
        }

        public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType)
        {
            return destinationType == typeof(InstanceDescriptor) || base.CanConvertTo(context, destinationType);
        }

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if (value is string text)
            {
                text = text.Trim();
                string[] splitText = text.Split(',');
                if (splitText.Length == 2)
                {
                    string potentialLongitude = splitText[0].Trim();
                    if (potentialLongitude.StartsWith('['))
                    {
                        potentialLongitude = potentialLongitude[1..];
                    }
                    
                    string potentialLatitude = splitText[1].Trim();
                    if (potentialLatitude.EndsWith(']'))
                    {
                        potentialLatitude = potentialLatitude[0..^1];
                    }

                    if (double.TryParse(potentialLongitude, out double longitude) && double.TryParse(potentialLatitude, out double latitude))
                    {
                        return new Position(longitude, latitude);
                    }
                }
            }

            return base.ConvertFrom(context, culture, value);
        }

        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is Position p1)
            {
                return $"[{p1.Longitude}, {p1.Latitude}]";
            }

            if (destinationType == typeof(InstanceDescriptor) && value is Position p2)
            {
                return new InstanceDescriptor(typeof(Position).GetConstructor(new Type[] { typeof(double), typeof(double) }),
                    new object[] { p2.Longitude, p2.Latitude });
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }
    }
}
