module Marquee
  open OpenQA.Selenium

  type BrowserDirectoryOption =
  | CurrentDirectory
  | SpecificDirectory of string

  type BrowserType =
  | Chrome of BrowserDirectoryOption
  | Firefox of BrowserDirectoryOption
  | PhantomJs of BrowserDirectoryOption

  type private WaitResult<'T> =
  | WaitSuccessful of 'T
  | WaitFailure of System.Exception

  type private ContinueFunction<'T> = unit -> WaitResult<'T>
  type WebElements = IWebElement array

  exception UnableToFindElementsWithSelectorException of string
  exception NoWebElementsDisplayedException of string
  exception WebElementsDoNotMatchSuppliedTextException of string
  exception WebElementIsReadOnlyException of IWebElement
  exception WebElementSelectDoesNotContainTextException of string
  exception WebElementIsNotCheckedException of IWebElement
  exception WebElementIsCheckedException of IWebElement
  exception NoOptionInSelectThatMatchesTextException of string
  exception OptionIsNotSelectedException of string
  exception AlertTextDoesNotEqualException of string
  exception BrowserPageIsNotExpectedUrlException of string

  let private wait (timeout : int) (continueFunction : ContinueFunction<'T>) =
    let stopwatch = System.Diagnostics.Stopwatch.StartNew()
    let rec testContinueFunction lastActivationTime =
      let elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds
      match (elapsedMilliseconds - lastActivationTime) >= 1000.0 with
      | true ->
          match continueFunction() with
          | WaitSuccessful result ->
            result
          | WaitFailure ex ->
            let lastActivationTime = stopwatch.Elapsed.TotalMilliseconds
            match elapsedMilliseconds - (float timeout) >= 0.001 with
            | true ->
              raise ex
            | false ->
              testContinueFunction lastActivationTime
      | false ->
        testContinueFunction lastActivationTime
    testContinueFunction -1000.0

  type BrowserConfiguration =
    {
      BrowserType : BrowserType
      ElementTimeout : int
      AssertionTimeout : int
    }

  type Browser =
    {
      Instance : OpenQA.Selenium.IWebDriver
      ElementTimeout : int
      AssertionTimeout : int
    }

    static member Create (configuration : BrowserConfiguration) : Browser =
      let getBrowserDirectory directoryOption =
        match directoryOption with
        | CurrentDirectory -> System.IO.Directory.GetCurrentDirectory()
        | SpecificDirectory dir -> dir
      let hideCommandPromptWindow = true
      let browserInstance =
        match configuration.BrowserType with
        | Chrome chromeDir ->
          let chromeDir = getBrowserDirectory chromeDir
          let chromeDriverService =
            let service = Chrome.ChromeDriverService.CreateDefaultService(chromeDir)
            service.HideCommandPromptWindow <- hideCommandPromptWindow
            service
          let options = Chrome.ChromeOptions()
          options.AddArgument("--disable-extensions")
          options.AddArgument("disable-infobars")
          options.AddArgument("test-type") //https://code.google.com/p/chromedriver/issues/detail?id=799
          new OpenQA.Selenium.Chrome.ChromeDriver(chromeDriverService , options) :> IWebDriver
        | Firefox firefoxDir ->
          let firefoxDir = getBrowserDirectory firefoxDir
          let firefoxService =
            let service = OpenQA.Selenium.Firefox.FirefoxDriverService.CreateDefaultService(firefoxDir)
            service.HideCommandPromptWindow <- hideCommandPromptWindow
            service
          new OpenQA.Selenium.Firefox.FirefoxDriver(firefoxService) :> IWebDriver
        | PhantomJs phantomDir ->
          let phantomDir = getBrowserDirectory phantomDir
          let phantomJsDriverService =
            let service = PhantomJS.PhantomJSDriverService.CreateDefaultService(phantomDir)
            service.HideCommandPromptWindow <- hideCommandPromptWindow
            service
          new OpenQA.Selenium.PhantomJS.PhantomJSDriver(phantomJsDriverService) :> IWebDriver
      {
        Instance = browserInstance
        ElementTimeout = configuration.ElementTimeout
        AssertionTimeout = configuration.AssertionTimeout
       }

    member this.Quit () =
      this.Instance.Quit()

    member private this.WaitForAssertion assertionFunction =
      let waitForAssertion timeout assertionFunc =
        let continueFunction : ContinueFunction<unit> = fun () ->
          try
            assertionFunc ()
            WaitSuccessful ()
          with
          | ex -> WaitFailure ex
        wait timeout continueFunction
      waitForAssertion this.AssertionTimeout assertionFunction

    member this.FindElements cssSelector =
      let searchContext : ISearchContext = this.Instance :> ISearchContext
      let findElementsByCssSelector timeout cssSelector (browser : ISearchContext) =
        let continueFunction : ContinueFunction<WebElements> =
          fun _ ->
            let elements = browser.FindElements((By.CssSelector cssSelector)) |> Seq.toArray
            match elements with
            | [||] -> WaitFailure <| UnableToFindElementsWithSelectorException cssSelector
            | elements ->
              WaitSuccessful elements
        let elements =
          wait timeout continueFunction
        elements
      let elements = findElementsByCssSelector this.ElementTimeout cssSelector searchContext
      elements

    member this.Click cssSelector =
      let elements = this.FindElements cssSelector
      elements |> Array.iter(fun element -> element.Click())

    member this.Displayed cssSelector =
      let isShown (element : IWebElement) =
        let opacity = element.GetCssValue("opacity")
        let display = element.GetCssValue("display")
        display <> "none" && opacity = "1"
      let assertionFunction = fun () ->
        let elements = this.FindElements cssSelector
        match elements |> Array.filter(isShown) with
        | [||] -> raise <| NoWebElementsDisplayedException cssSelector
        | _ -> ()
      this.WaitForAssertion assertionFunction

    member this.Url (url : string) =
      this.Instance.Navigate().GoToUrl(url)

    member this.IsOnPage testUrl =
      let assertionFunction = fun () ->
        let currentUrl = this.Instance.Url
        match currentUrl = testUrl with
        | true ->
          ()
        | false ->
          raise <| BrowserPageIsNotExpectedUrlException(sprintf "Expected %s but browser was on %s" testUrl currentUrl)
      this.WaitForAssertion assertionFunction

    static member private ReadText (element : IWebElement) =
        match element.TagName.ToLower() with
        | "input" | "textarea" -> element.GetAttribute("value")
        | _ ->
          element.Text

    member this.ElementTextEquals testText cssSelector =
      let elements = this.FindElements cssSelector
      //TODO replace filter with map filter operation
      //for efficiency
      let assertionFunction = fun () ->
        match elements |> Array.filter(fun element -> (Browser.ReadText element) <> testText) with
        | [||] -> ()
        | elements ->
          let elements = elements |> Array.map(Browser.ReadText)
          raise <| WebElementsDoNotMatchSuppliedTextException (sprintf "%s is not found in %A" testText elements)
      this.WaitForAssertion assertionFunction

    member this.ClearInput cssSelector =
      let elements = this.FindElements cssSelector
      let clear (element : IWebElement) =
        match element.GetAttribute("readonly") with
        | "true" -> raise <| WebElementIsReadOnlyException element
        | _ -> element.Clear()
      elements |> Array.iter clear

    member this.SetInput text cssSelector =
      let elements = this.FindElements cssSelector
      let clear (element : IWebElement) =
        match element.GetAttribute("readonly") with
        | "true" -> raise <| WebElementIsReadOnlyException element
        | _ ->
          element.Clear()
          element.SendKeys(text)
      elements |> Array.iter clear

    member this.TextExistsInElements text cssSelector =
      let assertionFunction = fun () ->
        let elements = this.FindElements cssSelector
        let testElementText element =
          (Browser.ReadText element) <> text
        match elements  |> Array.tryFind testElementText with
        | Some _ -> ()
        | None -> raise <| WebElementSelectDoesNotContainTextException text
      this.WaitForAssertion assertionFunction

    member this.CheckElements cssSelector =
      let elements = this.FindElements cssSelector
      let check (element : IWebElement) =
        match element.Selected with
        | true -> ()
        | false -> element.Click()
      elements |> Array.iter check

    member this.UnCheckElements cssSelector =
      let elements = this.FindElements cssSelector
      let check (element : IWebElement) =
        match element.Selected with
        | true -> element.Click()
        | false -> ()
      elements |> Array.iter check

    member this.AreElementsChecked cssSelector =
      let assertionFunction = fun () ->
        let elements = this.FindElements cssSelector
        let testElement (element : IWebElement) =
          match element.Selected with
          | true -> ()
          | false -> raise <| WebElementIsNotCheckedException element
        elements |> Array.iter testElement
      this.WaitForAssertion assertionFunction

    member this.AreElementsUnChecked cssSelector =
      let assertionFunction = fun () ->
        let elements = this.FindElements cssSelector
        let testElement (element : IWebElement) =
          match element.Selected with
          | true -> raise <| WebElementIsCheckedException element
          | false -> ()
        elements |> Array.iter testElement
      this.WaitForAssertion assertionFunction

    member this.SetSelectOption option selectCssSelector =
      let cssSelector = sprintf "%s option" selectCssSelector
      let elements =
        this.FindElements cssSelector
        |> Array.filter(fun element -> element.Text = option)
      match elements with
      | [||] -> raise <| NoOptionInSelectThatMatchesTextException option
      | elements -> elements |> Array.iter(fun element -> element.Click())

    member this.IsOptionSelected option selectCssSelector =
      let assertionFunction = fun () ->
        let cssSelector = sprintf "%s option" selectCssSelector
        let elements =
          this.FindElements cssSelector
        match elements |> Array.tryFind(fun element -> element.Text = option) with
        | None -> raise <| NoOptionInSelectThatMatchesTextException option
        | Some element ->
          match element.Selected with
          | true -> ()
          | false -> raise <| OptionIsNotSelectedException option
      this.WaitForAssertion assertionFunction

    member this.AlertTextEquals text  =
      let assertionFunction = fun () ->
        let alert = this.Instance.SwitchTo().Alert()
        match alert.Text = text with
        | true -> ()
        | false -> raise <| AlertTextDoesNotEqualException text
      this.WaitForAssertion assertionFunction

    member this.AcceptAlert () =
      let alert = this.Instance.SwitchTo().Alert()
      alert.Accept()

    member this.DismissAlert () =
      let alert = this.Instance.SwitchTo().Alert()
      alert.Dismiss()
