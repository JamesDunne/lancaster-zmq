using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.IsolatedStorage;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WellDunne.LanCaster.GUI
{
    #region XAML Binding Converters for display

    public class ClientFileCompletionStatusConverter : IValueConverter
    {
        #region IValueConverter Members

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return "FIXME";
#if false
            ClientFileCompletionStatus status = (ClientFileCompletionStatus)value;

            switch (status)
            {
                case ClientFileCompletionStatus.Completed: return "Completed";
                case ClientFileCompletionStatus.HashMismatch: return "Hash Mismatch";
                case ClientFileCompletionStatus.InProgress: return "In Progress";
                case ClientFileCompletionStatus.Missing: return "File Missing";
                case ClientFileCompletionStatus.NotReceived: return "Not Received";
                case ClientFileCompletionStatus.Verified: return "Verified";
                case ClientFileCompletionStatus.Verifying: return "Verifying";
                case ClientFileCompletionStatus.Partial: return "Partial";
                default:
                    return null;
            }
#endif
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }

        #endregion
    }

    public class FileSizeConverter : IValueConverter
    {
        #region IValueConverter Members

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            long size = (long)value;

            if (size >= 1073741824L)
            {
                return String.Concat(
                    (size / 1073741824M).ToString("###,###,###.##"),
                    " GB"
                );
            }
            else if (size >= 1048576L)
            {
                return String.Concat(
                    (size / 1048576M).ToString("#,###.##"),
                    " MB"
                );
            }
            else if (size >= 1024L)
            {
                return String.Concat(
                    (size / 1024M).ToString("#,###.##"),
                    " KB"
                );
            }
            else
            {
                return String.Concat(
                    (size).ToString("#,###.##"),
                    " bytes"
                );
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }

        #endregion
    }

    public class TransferRateConverter : IValueConverter
    {
        #region IValueConverter Members

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            decimal rate = (decimal)value;
            if (rate >= 1048576)
            {
                return String.Concat(
                    (rate / 1048576.0M).ToString("###,###,##0.00"),
                    " MB/sec"
                );
            }
            else if (rate >= 1024)
            {
                return String.Concat(
                    (rate / 1024.0M).ToString("###,##0.00"),
                    " KB/sec"
                );
            }
            else
            {
                return String.Concat(
                    (rate).ToString("##,##0"),
                    "  B/sec"
                );
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }

        #endregion
    }

    public class ByteArrayBASE64Converter : IValueConverter
    {
        #region IValueConverter Members

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            byte[] hash = (byte[])value;

            if (hash == null) return "N/A";
            return System.Convert.ToBase64String(hash, Base64FormattingOptions.None);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }

        #endregion
    }

    #endregion
}
