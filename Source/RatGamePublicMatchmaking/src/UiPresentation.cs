using System;
using System.Text;

namespace RatGamePublicMatchmaking
{
    // Presentation only. Admission decisions remain in Safety.cs / Joining.cs.
    internal static class UiPresentation
    {
        internal static string HostName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unnamed host";
            var value = new StringBuilder();
            foreach (char c in name!)
            {
                if (!char.IsControl(c)) value.Append(c);
                if (value.Length >= 80) break;
            }
            return value.Length == 0 ? "Unnamed host" : value.ToString();
        }

        internal static string RowAction(int members, int maximum, bool available, bool joining) =>
            maximum > 0 && members >= maximum ? "Full" : !available ? "Unavailable" : joining ? "Please wait" : "Join";

        internal static string Hosting(bool requested, bool advertised, bool cleanup) => cleanup ? "Public Hosting: withdrawing..." :
            advertised ? "Public Hosting: ON - advertised" : requested ? "Public Hosting: ON - next host" : "Public Hosting: OFF";
    }
}
