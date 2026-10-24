using CoasterpediaServices.ArchiveBot.Links;
using Xunit;

namespace CoasterpediaServices.ArchiveBot.Tests;

// Redirects found by following the wiki's own citations on 2026-10-24.
public class SoftNotFoundTests
{
    private static bool Soft(string from, string to) => SoftNotFound.IsSoftNotFound(new Uri(from), new Uri(to));

    [Theory]
    // The site's home page.
    [InlineData("https://www.rideentertainment.com/wp-content/cache/all/projects/index.html", "https://rideentertainment.com/")]
    [InlineData("https://www.themeparktourist.com/news/20090405/79/sesame-street-opens-busch-gardens-europe", "https://parklore.com/")]
    [InlineData("https://park.example/rides/old-coaster", "https://park.example/en/")]
    [InlineData("https://park.example/rides/old-coaster", "https://park.example/index.php")]
    // A landing page: Cedar Fair's parks moving to sixflags.com.
    [InlineData("https://www.cagreatamerica.com/blog/2020/all-these-improvements-will-leave-your-head-spinning", "https://www.sixflags.com/blog")]
    [InlineData("https://www.visitkingsisland.com/blog/2018/march/congo-falls-celebrates-30-years-in-2018", "https://www.sixflags.com/blog")]
    [InlineData("https://www.kingsdominion.com/blog/big-changes-coming-to-candy-apple-grove", "https://www.sixflags.com/blog")]
    [InlineData("https://www.cedarpoint.com/play/rides/top-thrill-dragster", "https://www.sixflags.com/cedarpoint")]
    [InlineData("https://www.canadaswonderland.com/play/rides/wilde-knight-mares", "https://www.sixflags.com/canadaswonderland")]
    // A section above the article.
    [InlineData("https://www.wsmv.com/news/years-after-closing-some-opryland-rides-live-on/article_dccc303e-5d9e-56f7-abc0-6c22865c7a3a.html", "https://www.wsmv.com/content/news/")]
    // A not-found page.
    [InlineData("https://park.example/rides/old-coaster", "https://park.example/404.html")]
    [InlineData("https://park.example/rides/old-coaster", "https://park.example/errors/page-not-found?from=rides")]
    public void A_removed_page_is_a_soft_404(string from, string to)
    {
        Assert.True(Soft(from, to));
    }

    [Theory]
    // Moved, keeping its slug or ID.
    [InlineData("https://www.zamperla.com/zpress/discovery-30-paultons-park/", "https://www.zamperla.com/news/discovery-30-paultons-park")]
    [InlineData("http://www.zamperla.com/products/baloon-race/", "https://www.zamperla.com/products/baloon-race")]
    [InlineData("https://www.clactonpier.co.uk/news-blog/content/stella-s-revenge-to-make-way-for-new-ride", "https://www.clactonpier.co.uk/news/stellas-revenge-to-make-way-for-new-ride/")]
    [InlineData("https://abc7news.com/santa-cruz-boardwalk-wheel-ferris-beach/3235318/", "https://abc7news.com/post/santa-cruz-beach-boardwalk-closes-ferris-wheel-after-nearly-60-years-of-thrills/3235318/")]
    [InlineData("http://articles.latimes.com/2011/may/19/local/la-trb-universal-studios-despicable-me-05201119", "https://www.latimes.com/travel/la-xpm-2011-may-19-la-trb-universal-studios-despicable-me-05201119-story.html")]
    [InlineData("https://www.sandiegouniontribune.com/business/tourism/sdut-seaworld-aquatica-water-park-opens-2013may29-htmlstory.html", "https://www.sandiegouniontribune.com/2013/05/29/aquatica-is-a-major-sea-change-for-water-park/")]
    [InlineData("https://www.rtlnieuws.nl/economie/beurs/artikel/4134096/achtbaanbouwer-vekoma-uit-nederland-japanse-handen", "https://www.rtl.nl/economie/beurs/artikel/4134096/achtbaanbouwer-vekoma-uit-nederland-japanse-handen")]
    [InlineData("http://pleasurewoodhills.com/rides-attractions/thrill-rides/wave-swinger/", "https://pleasurewoodhills.com/ride/waveswinger/")]
    [InlineData("https://kulturgut-volksfest.de/enzyklopaedie/zeppelin/", "https://kulturgut-volksfest.de/enzyklopaedie/zeppelin-i-und-ii/")]
    [InlineData("http://cdm15847.contentdm.oclc.org/cdm/singleitem/collection/p15847coll3/id/14322/rec/13", "https://cdm15847.contentdm.oclc.org/digital/collection/p15847coll3/id/14322")]
    [InlineData("https://www.newspapers.com/clip/52574546/six-flags-over-georgia-open-june-16/", "https://www.newspapers.com/article/52574546/six-flags-over-georgia-open-june-16/")]
    [InlineData("https://twitter.com/Oaks_Park/status/964323431071195137", "https://x.com/Oaks_Park/status/964323431071195137")]
    [InlineData("https://www.facebook.com/watch/?v=1265886640102780", "https://www.facebook.com/BelleCityAmusements/videos/1265886640102780/")]
    [InlineData("http://amusementparkstuff.blogspot.co.uk/2015/04/uh-2015-seminole-county-fair.html", "http://amusementparkstuff.blogspot.com/2015/04/uh-2015-seminole-county-fair.html")]
    [InlineData("http://www.gerstlauer-rides.de/contact/", "https://www.gerstlauer-rides.de/en/service/contact")]
    [InlineData("https://park.example/rides/old-coaster", "https://newpark.example/old-coaster")]
    // A home page that moved is still the home page.
    [InlineData("http://www.intpark.it/", "https://www.roller-coaster.it/")]
    [InlineData("http://www.hansapark.de/", "https://www.hansapark.de/index?language=de")]
    [InlineData("http://www.carowinds.com/", "https://www.sixflags.com/carowinds")]
    [InlineData("http://www.sixflags.com.mx/", "https://www.sixflags.com/mexico")]
    [InlineData("http://hongkongdisneyland.com/", "https://www.hongkongdisneyland.com/?located=true")]
    [InlineData("https://pleasure.hitachikaihin.jp/", "https://www.hitachikaihin.jp/pleasure/")]
    [InlineData("https://park.example/en", "https://park.example/")]
    [InlineData("https://park.example/index.html", "https://park.example/")]
    // An article about something not found keeps its own words.
    [InlineData("http://news.example/2019/treasure-not-found-at-park", "https://news.example/2019/treasure-not-found-at-park")]
    public void A_moved_page_is_not(string from, string to)
    {
        Assert.False(Soft(from, to));
    }
}
