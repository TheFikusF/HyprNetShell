using System.Security;
using System.Text;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Games.Solitaire;

internal sealed class SolitaireCardTextures
{
    internal const int Width = 60;
    internal const int Height = 86;

    private readonly RawImageData[,] _faces = new RawImageData[4, 13];

    internal RawImageData Back
    {
        get;
    }

    internal SolitaireCardTextures()
    {
        Back = Bake("back", BackSvg());
        foreach (var suit in Enum.GetValues<SolitaireSuit>())
        {
            for (var rank = 1; rank <= 13; rank++)
            {
                _faces[(int)suit, rank - 1] = Bake($"{suit}-{rank}", FaceSvg(suit, rank));
            }
        }
    }

    internal RawImageData Face(SolitaireCard card) => _faces[(int)card.Suit, card.Rank - 1];

    private static RawImageData Bake(string name, string svg)
    {
        var source = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        var raster = new SvgAsset($"generated/solitaire/{name}.svg", source).Rasterize();
        return new RawImageData(raster.Width, raster.Height, raster.Pixels);
    }

    private static string FaceSvg(SolitaireSuit suit, int rank)
    {
        var rankText = rank switch {
            1 => "A",
            11 => "J",
            12 => "Q",
            13 => "K",
            _ => rank.ToString()
        };
        var symbol = suit switch {
            SolitaireSuit.Diamonds => "♦",
            SolitaireSuit.Clubs => "♣",
            SolitaireSuit.Hearts => "♥",
            SolitaireSuit.Spades => "♠",
            _ => "?",
        };
        var color = suit is SolitaireSuit.Diamonds or SolitaireSuit.Hearts ? "#c92f3b" : "#20242b";
        rankText = SecurityElement.Escape(rankText) ?? rankText;
        symbol = SecurityElement.Escape(symbol) ?? symbol;

        return $$"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{{Width}}" height="{{Height}}" viewBox="0 0 {{Width}} {{Height}}">
              <defs>
                <linearGradient id="paper" x1="0" y1="0" x2="1" y2="1">
                  <stop offset="0" stop-color="#fffef8"/>
                  <stop offset="0.58" stop-color="#f6f3e8"/>
                  <stop offset="1" stop-color="#e7e1ce"/>
                </linearGradient>
                <filter id="inset" x="-10%" y="-10%" width="120%" height="120%">
                  <feDropShadow dx="0" dy="1" stdDeviation="0.7" flood-color="#ffffff" flood-opacity="0.9"/>
                </filter>
              </defs>
              <rect x="0.75" y="0.75" width="58.5" height="84.5" rx="5.5" fill="url(#paper)" stroke="#30343a" stroke-width="1.5"/>
              <rect x="3" y="3" width="54" height="80" rx="3.5" fill="none" stroke="#ffffff" stroke-opacity="0.72"/>
              <g fill="{{color}}" font-family="DejaVu Sans, sans-serif" font-weight="700" filter="url(#inset)">
                <text x="5" y="17" font-size="15">{{rankText}}</text>
                <text x="6" y="31" font-size="13">{{symbol}}</text>
                <text x="30" y="53" font-size="31" text-anchor="middle" dominant-baseline="middle">{{symbol}}</text>
                <g transform="rotate(180 30 43)">
                  <text x="5" y="17" font-size="15">{{rankText}}</text>
                  <text x="6" y="31" font-size="13">{{symbol}}</text>
                </g>
              </g>
            </svg>
            """;
    }

    private static string BackSvg() => $$"""
        <svg xmlns="http://www.w3.org/2000/svg" width="{{Width}}" height="{{Height}}" viewBox="0 0 {{Width}} {{Height}}">
          <defs>
            <linearGradient id="blue" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" stop-color="#2c68ad"/>
              <stop offset="0.55" stop-color="#174b87"/>
              <stop offset="1" stop-color="#0c315f"/>
            </linearGradient>
            <pattern id="weave" width="8" height="8" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
              <path d="M0 1h8M0 5h8" stroke="#9fc9ef" stroke-width="1" stroke-opacity="0.42"/>
              <path d="M1 0v8M5 0v8" stroke="#071d3b" stroke-width="1" stroke-opacity="0.28"/>
            </pattern>
          </defs>
          <rect x="0.75" y="0.75" width="58.5" height="84.5" rx="5.5" fill="#e7edf4" stroke="#d7e5f2" stroke-width="1.5"/>
          <rect x="3.5" y="3.5" width="53" height="79" rx="3.5" fill="url(#blue)" stroke="#153a69"/>
          <rect x="6" y="6" width="48" height="74" rx="2" fill="url(#weave)" stroke="#b7d8f5" stroke-opacity="0.75"/>
          <path d="M30 17 43 30 30 43 17 30Zm0 26 10 10-10 10-10-10Z" fill="none" stroke="#d7ecff" stroke-width="1.5" stroke-opacity="0.72"/>
        </svg>
        """;
}
