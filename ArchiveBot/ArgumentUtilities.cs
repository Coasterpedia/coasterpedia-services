using MarketAlly.IronWiki.Nodes;

namespace CoasterpediaServices.ArchiveBot;

public static class ArgumentUtilities
{
    public static TemplateArgument CreateArgument(string key, string value)
    {
        return new TemplateArgument { Name = CreateDocument(key), Value = CreateDocument(value) };
    }

    private static WikitextDocument CreateDocument(string property)
    {
        var name = new WikitextDocument();
        var para = new Paragraph();
        para.Inlines.Add(new PlainText(property));
        name.Lines.Add(para);
        return name;
    }
}