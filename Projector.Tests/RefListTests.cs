using CoasterpediaServices.Projector.Mapping;
using Xunit;

namespace CoasterpediaServices.Projector.Tests;

/// <summary>Wikitext.RefList against cells as stored on 2026-10-13 (DR-010 §12.18).</summary>
public class RefListTests
{
    private static (List<string> Names, List<string> Dropped) Split(string cell)
    {
        var dropped = new List<string>();
        var names = Wikitext.RefList(cell, dropped.Add).Select(x => x.Ref.Name).ToList();
        return (names, dropped);
    }

    [Theory]
    // Links, however they are joined.
    [InlineData("[[John A. Miller]] <br> [[Harry C. Baker]]", new[] { "John A. Miller", "Harry C. Baker" })]
    [InlineData("[[Dennis McNulty]], [[Larry Bill]]", new[] { "Dennis McNulty", "Larry Bill" })]
    [InlineData("[[Michael Eliasson]] and [[Mikael Andersson]]", new[] { "Michael Eliasson", "Mikael Andersson" })]
    [InlineData("[[Don Rosser]] <br> [[John C. Allen]] <br>[[William L. Cobb]]", new[] { "Don Rosser", "John C. Allen", "William L. Cobb" })]
    // A comma inside a link is part of the name.
    [InlineData("[[The Gravity Group, LLC]]", new[] { "The Gravity Group, LLC" })]
    [InlineData("[[Curtis D. Summers|Curtis D. Summers, Inc]]", new[] { "Curtis D. Summers, Inc" })]
    // Notes beside a link go: dates, shares, a role.
    [InlineData("[[Great Coasters International]] (2016-present) <br> [[Philadelphia Toboggan Company]] (1998-2015)",
        new[] { "Great Coasters International", "Philadelphia Toboggan Company" })]
    [InlineData("[[Herschend]] (1976-present)<br>[[wikipedia:Dolly Parton|Dolly Parton]] (1986-present)", new[] { "Herschend", "Dolly Parton" })]
    [InlineData("[[Skyline Attractions|Skyline Design]] <br>([[Dustin Sloane]])", new[] { "Skyline Design", "Dustin Sloane" })]
    // Plain names: a piece of their own, split at commas unless a suffix follows.
    [InlineData("Arrow Development Company Inc<br />Arrow-Huss Inc", new[] { "Arrow Development Company Inc", "Arrow-Huss Inc" })]
    [InlineData("Leisure Sales, Rutland Fabrications", new[] { "Leisure Sales", "Rutland Fabrications" })]
    [InlineData("Arnold Neble, Charles Walker, George Long Jr.", new[] { "Arnold Neble", "Charles Walker", "George Long Jr." })]
    [InlineData("The Gravity Group, LLC", new[] { "The Gravity Group, LLC" })]
    [InlineData("Clair Hain, Jr.", new[] { "Clair Hain, Jr." })]
    [InlineData("Okamoto Co., Ltd.", new[] { "Okamoto Co., Ltd." })]
    [InlineData("Soriani and Moser", new[] { "Soriani and Moser" })]
    // A plain piece beside a linked one keeps its own line.
    [InlineData("[[Parques Reunidos]] (95%)<br>Warner Bros. Discovery Global Brands and Experiences (5%)",
        new[] { "Parques Reunidos", "Warner Bros. Discovery Global Brands and Experiences" })]
    [InlineData("[[Ing.-Büro Stengel GmbH]] <br> Walt Disney Imagineering", new[] { "Ing.-Büro Stengel GmbH", "Walt Disney Imagineering" })]
    [InlineData("[[Sega|Sega AM5]]<br>Family Entertainment <small>(''Spin Bullet'' renovation)</small>", new[] { "Sega AM5", "Family Entertainment" })]
    // Placeholders name nobody.
    [InlineData("??? (1933-~1972) <br> [[Philadelphia Toboggan Coasters]] (~1973-2006)", new[] { "Philadelphia Toboggan Coasters" })]
    [InlineData("Unknown", new string[0])]
    [InlineData("", new string[0])]
    public void Splits_a_cell_into_names(string cell, string[] expected)
    {
        var (names, dropped) = Split(cell);
        Assert.Equal(expected, names);
        Assert.Empty(dropped);
    }

    [Theory]
    [InlineData("Bankrupt December 3, 2001, bought by [[S&S Worldwide|S&S Power]]", "S&S Power")]
    [InlineData("[[Big Country Motioneering]], Robert Staveley", "Big Country Motioneering")]
    [InlineData("[[Custom Coasters International, Inc.]], in house", "Custom Coasters International, Inc.")]
    [InlineData("Owners of [[Powers Great American Midways]] (co-owner)", "Powers Great American Midways")]
    public void Text_beside_a_link_that_is_more_than_a_note_is_reported(string cell, string link)
    {
        var (names, dropped) = Split(cell);
        Assert.Equal([link], names);
        Assert.Single(dropped);
    }

    [Fact]
    public void The_same_page_twice_is_one_ref_and_targets_are_kept()
    {
        var refs = Wikitext.RefList("[[Six Flags]] <br> [[Six Flags|Premier Parks]]");
        Assert.Single(refs);
        Assert.Equal("Six Flags", refs[0].Target);
    }

    [Fact]
    public void Labels_strip_split_and_deduplicate()
    {
        Assert.Equal(["Kiddie", "Spinning"], Wikitext.Labels(["Kiddie", "", "[[Spinning]]", "kiddie", "unknown", null]));
        Assert.Equal(["Frisbee", "Pendulum"], Wikitext.Labels(["Frisbee<br>Pendulum"]));
    }
}
