using System;
using System.Drawing;
using System.Windows.Forms;

namespace DownloadSearchAudio
{
    // Dark palette adapted from YouTubeDownloader-Net10 (Theme in MainForm.cs)
    // and matched to the mockup Assets\front view panel app.png: dark background,
    // card surfaces and a GREEN primary accent (the source used a blue accent).
    internal static class Theme
    {
        public const float BaseSize = 9F;

        public static readonly Font Regular = new Font("Segoe UI", BaseSize);
        public static readonly Font RegularBold = new Font("Segoe UI", BaseSize, FontStyle.Bold);
        public static readonly Font CardTitle = new Font("Segoe UI", 12F, FontStyle.Bold);
        public static readonly Font CardSubtitle = new Font("Segoe UI", 8.75F);
        public static readonly Font AppTitle = new Font("Segoe UI", 12.5F, FontStyle.Bold);
        public static readonly Font AppSubtitle = new Font("Segoe UI", 8.25F);
        public static readonly Font Glyph = new Font("Segoe MDL2 Assets", 10F);
        public static readonly Font GlyphSmall = new Font("Segoe MDL2 Assets", 9F);
        public static readonly Font GlyphLarge = new Font("Segoe MDL2 Assets", 14F);
        public static readonly Font Small = new Font("Segoe UI", 8.25F);

        // Window and surfaces
        public static readonly Color Back = Color.FromArgb(24, 26, 29);        // window background
        public static readonly Color Card = Color.FromArgb(32, 35, 40);        // card surface
        public static readonly Color Input = Color.FromArgb(43, 47, 53);       // text boxes
        public static readonly Color Button = Color.FromArgb(48, 52, 59);      // neutral buttons
        public static readonly Color ButtonHover = Color.FromArgb(62, 67, 76); // neutral hover
        public static readonly Color Border = Color.FromArgb(55, 60, 68);      // borders
        public static readonly Color HeaderBar = Color.FromArgb(30, 33, 37);   // title bar

        // Text
        public static readonly Color Light = Color.FromArgb(235, 238, 240);
        public static readonly Color Dim = Color.FromArgb(150, 158, 166);
        public static readonly Color Faint = Color.FromArgb(110, 118, 126);

        // Accents (green primary per mockup)
        public static readonly Color Green = Color.FromArgb(46, 204, 113);       // primary buttons
        public static readonly Color GreenHover = Color.FromArgb(60, 216, 128);
        public static readonly Color GreenDark = Color.FromArgb(34, 139, 84);    // hover on dark-green
        public static readonly Color GreenSoft = Color.FromArgb(26, 61, 46);     // icon chip background
        public static readonly Color GreenText = Color.FromArgb(88, 214, 141);   // green text/status
        public static readonly Color Red = Color.FromArgb(255, 107, 107);
        public static readonly Color Orange = Color.FromArgb(255, 184, 77);

        public static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = ButtonHover;
            b.BackColor = Button;
            b.ForeColor = Light;
        }

        public static void StyleGreen(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Green;
            b.FlatAppearance.MouseOverBackColor = GreenHover;
            b.BackColor = Green;
            b.ForeColor = Color.FromArgb(16, 24, 20);
        }

        public static void StyleInput(TextBox t)
        {
            t.BackColor = Input;
            t.ForeColor = Light;
            t.BorderStyle = BorderStyle.FixedSingle;
        }
    }
}
