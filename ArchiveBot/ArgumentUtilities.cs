using MarketAlly.IronWiki.Nodes;

namespace CoasterpediaServices.ArchiveBot;

public static class ArgumentUtilities
{
    public static TemplateArgument CreateArgument(string key, string value)
    {
        return new TemplateArgument { Name = CreateDocument(key), Value = CreateDocument(value) };
    }

    public static void UpdateArgument(Template template, string key, string value)
    {
        var currentValue = template.Arguments.Where(x => x.Name?.ToString().Trim().ToLower() == key).ToList();
        switch (currentValue.Count)
        {
            case > 1:
            {
                foreach (var argument in currentValue)
                {
                    template.Arguments.Remove(argument);
                }

                break;
            }
            case 1:
            {
                if (currentValue.Single().Value.ToString() == value)
                {
                    return;
                }

                template.Arguments.Remove(currentValue.Single());

                break;
            }
        }

        template.Arguments.Add(CreateArgument(key, value));
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