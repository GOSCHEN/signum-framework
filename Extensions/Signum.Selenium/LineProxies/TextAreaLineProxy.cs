using OpenQA.Selenium;

namespace Signum.Selenium.LineProxies;

public class TextAreaLineProxy : BaseLineProxy
{
    public TextAreaLineProxy(IWebElement element, PropertyRoute route)
       : base(element, route)
    {
    }

    public override object? GetValueUntyped() => this.GetValue();
    public override void SetValueUntyped(object? value) => this.SetValue((string?)value);
    public WebElementLocator TextAreaLocator => this.Element.WithLocator(By.CssSelector("textarea"));

    public void SetValue(string? value)
    {
        TextAreaLocator.Find().SafeSendKeys(value);
    }

    public string GetValue()
    {
        var textLine = TextAreaLocator.Find();

        return /*textLine.GetDomAttribute("data-value") ??*/ textLine.GetDomProperty("value")!;
    }


    public bool IsReadonly()
    {
        var element = TextAreaLocator.Find();

        return element.GetDomProperty("disabled") == "true";
    }
}
