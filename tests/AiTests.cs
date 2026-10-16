using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    /// <summary>Answers HTTP requests the way a test says, and keeps what was sent.</summary>
    private sealed class FakeAiHttp(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string Body)> Sent = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
            lock (Sent) Sent.Add((request, body));
            return answer(request, body);
        }
    }
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json) };
    /// <summary>An AI that answers from a list and remembers what it was shown.</summary>
    private sealed class FakeVision(params string[] answers) : IAiVision
    {
        private int _next;
        public readonly List<(string Question, int Width, int Height)> Asked = [];
        public string Ask(string question, PixelImage? picture, CancellationToken token)
        {
            lock (Asked) Asked.Add((question, picture?.Width ?? 0, picture?.Height ?? 0));
            return answers[Math.Min(_next++, answers.Length - 1)];
        }
    }

    private static async Task AiEngineTests()
    {
        var shot = new PixelImage(3840, 2160, Enumerable.Repeat(0xFF204080u, 3840 * 2160).ToArray());
        var small = AiVision.Shrink(shot, AiVision.MaxSide);
        Check(small.Width == 1280 && small.Height == 720 && small.Pixels[0] == 0xFF204080 && AiVision.Shrink(Button(), 600) is { Width: 24 },
            "a 4K screen is made 1280 wide for the AI, keeping its colors; small pictures stay as they are");
        Check(AiVision.YesNo("Yes.") == true && AiVision.YesNo("no, it is closed") == false && AiVision.YesNo("Tak") == true && AiVision.YesNo("Maybe") == null,
            "yes and no are read from the start of an answer, in English or Polish");
        Check(AiVision.NumberIn("1,250 coins") == 1250 && AiVision.NumberIn("about -3.5") == -3.5 && AiVision.NumberIn("none") == null, "the first number in an answer is read");

        // Ollama on this PC.
        var ollama = new FakeAiHttp((r, _) => Reply("""{ "message": { "role": "assistant", "content": "<think>hmm</think>\"Yes.\"" } }"""));
        var vision = new AiVision(new AiSetup(AiProvider.Ollama, "gemma3", "http://localhost:11434/"), AiVision.Client(ollama));
        string answer = vision.Ask("Is the shop open?", Button(), CancellationToken.None);
        var sent = JsonNode.Parse(ollama.Sent[0].Body)!;
        Check(answer == "Yes." && ollama.Sent[0].Request.RequestUri!.ToString() == "http://localhost:11434/api/chat" && (string?)sent["model"] == "gemma3"
            && (string?)sent["messages"]![0]!["content"] == AiVision.Instructions && sent["messages"]![1]!["images"]![0]!.ToString().Length > 20 && (bool?)sent["stream"] == false,
            "Ollama gets the model, the instructions, the question and the picture, and the answer comes back without quotes or thinking");
        var missing = new AiVision(new AiSetup(AiProvider.Ollama, "llava"), AiVision.Client(new FakeAiHttp((_, _) => Reply("""{ "error": "model not found" }""", HttpStatusCode.NotFound))));
        Check(FailsWith<InvalidOperationException>(() => missing.Ask("Hi?", null, CancellationToken.None), "ollama pull llava"), "a model Ollama lacks says how to get it");
        var down = new AiVision(new AiSetup(AiProvider.Ollama, "llava"), AiVision.Client(new FakeAiHttp((_, _) => throw new HttpRequestException("refused"))));
        Check(FailsWith<InvalidOperationException>(() => down.Ask("Hi?", null, CancellationToken.None), "Start Ollama"), "Ollama not running says to start it");

        // OpenRouter online.
        var online = new FakeAiHttp((r, _) => r.Headers.Authorization?.Parameter == "sk-good"
            ? Reply("""{ "choices": [ { "message": { "content": " 42 " } } ] }""")
            : Reply("""{ "error": { "message": "No auth" } }""", HttpStatusCode.Unauthorized));
        var claude = new AiVision(new AiSetup(AiProvider.OpenRouter, "anthropic/claude-model", ApiKey: "sk-good"), AiVision.Client(online));
        answer = claude.Ask("How many coins?", Button(), CancellationToken.None);
        sent = JsonNode.Parse(online.Sent[0].Body)!;
        Check(answer == "42" && online.Sent[0].Request.RequestUri!.ToString() == "https://openrouter.ai/api/v1/chat/completions"
            && sent["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.ToString().StartsWith("data:image/png;base64,") && online.Sent[0].Request.Headers.Contains("X-Title"),
            "OpenRouter gets the key, the question and the picture as a PNG");
        var badKey = new AiVision(new AiSetup(AiProvider.OpenRouter, "x", ApiKey: "sk-bad"), AiVision.Client(online));
        Check(FailsWith<InvalidOperationException>(() => badKey.Ask("Hi?", null, CancellationToken.None), "did not accept the key"), "a wrong key says so");
        var noKey = new AiVision(new AiSetup(AiProvider.OpenRouter, "x"), AiVision.Client(online));
        Check(FailsWith<InvalidOperationException>(() => noKey.Ask("Hi?", null, CancellationToken.None), "Add your OpenRouter key") && online.Sent.Count == 2,
            "without a key nothing is sent");

        var list = new FakeAiHttp((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/models")
            ? Reply("""{ "data": [ { "id": "b/sees", "architecture": { "input_modalities": ["text", "image"] } }, { "id": "a/blind", "architecture": { "input_modalities": ["text"] } } ] }""")
            : Reply("""{ "models": [ { "name": "qwen2.5vl" }, { "name": "gemma3" } ] }"""));
        Check((await AiVision.ModelsAsync(new AiSetup(AiProvider.OpenRouter, ""), AiVision.Client(list))).SequenceEqual(["b/sees"])
            && (await AiVision.ModelsAsync(new AiSetup(AiProvider.Ollama, ""), AiVision.Client(list))).SequenceEqual(["gemma3", "qwen2.5vl"]),
            "the model list shows OpenRouter's models that can see, and the models Ollama has");

        Check(Secret.Reveal(Secret.Hide("sk-secret")) == "sk-secret" && Secret.Hide("sk-secret") != "sk-secret" && Secret.Reveal("not hidden") == "",
            "the key is kept encrypted, and one that cannot be read counts as none");
        Check(new AppSettings { Ai = false, AiModel = "x" }.AiSetup == null && new AppSettings { Ai = true, AiModel = " m " }.AiSetup is { Model: "m", Provider: AiProvider.Ollama },
            "macros reach the AI only while it is on");

        // An If that asks: the steps play when the AI says yes, or no if the If says so.
        var screen = new FakeScreen(640, 400);
        var driver = new FakeDriver();
        var fake = new FakeVision("Yes", "no");
        await using var executor = new MacroExecutor(driver, screen: screen, ai: () => fake);
        string failure = "";
        executor.ExecutionFailed += m => failure = m;
        var p = Profile(MacroBehaviorType.Sequence); p.RepeatCount = 2;
        p.Actions = new([new() { ActionType = MacroActionType.GroupStart, Block = BlockKind.If, Question = "Is the shop open?" }, Press("B"), End(), Else(), Press("C"), End()]);
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        Check(string.Join(",", driver.Events.Where(e => e.EndsWith(":down"))) == "K66:down,K67:down" && fake.Asked.Count == 2
            && fake.Asked[0].Question.StartsWith("Is the shop open?") && fake.Asked[0].Question.Contains("yes or no") && fake.Asked[0].Width == 640 && failure == "",
            "an If that asks plays its steps when the AI says yes and its Else when it says no, showing it the screen");
        await using var off = new MacroExecutor(driver, screen: screen, ai: () => null);
        off.ExecutionFailed += m => failure = m;
        p.RepeatCount = 1;
        await off.ToggleAsync(p); await Until(() => off.ActiveProfile == null);
        Check(failure == AiEyes.OffMessage, "with AI off, an If that asks stops the macro and says how to turn AI on");

        // Scripts ask with ask(), askYesNo() and askNumber().
        fake = new FakeVision("The Lucky Shop", "yes", "1,250 coins", "no number");
        await using var scripted = new MacroExecutor(driver, screen: screen, ai: () => fake);
        var logged = new List<string>();
        scripted.ScriptLogged += logged.Add;
        failure = "";
        scripted.ExecutionFailed += m => failure = m;
        var s = ScriptMacro("log(ask(\"Which shop?\"), askYesNo(\"Open?\", \"Play\"), askNumber(\"Coins?\", { x: 10, y: 20, width: 100, height: 50 }), askNumber(\"Gems?\", false))");
        s.Targets.Add(new ScreenTarget { Name = "Play", Kind = ScreenTargetKind.Color, Color = "#00FF00", Area = new(5, 5, 60, 40) });
        await scripted.ToggleAsync(s); await Until(() => scripted.ActiveProfile == null);
        Check(logged.SingleOrDefault() == "The Lucky Shop true 1250 null" && failure == "", "a script gets the AI's answer as text, yes as true, and a number");
        Check(fake.Asked.Select(a => (a.Width, a.Height)).SequenceEqual([(640, 400), (60, 40), (100, 50), (0, 0)]) && fake.Asked[2].Question.Contains("only with the number"),
            "a script shows the AI the screen, a target's area, an area of its own, or nothing");

        // An If that asks, in the macro file.
        p.Actions[0].Gone = true;
        string json = MacroFile.Write([p]);
        var back = MacroFile.Read(json).Single();
        Check(json.Contains("\"ask\": \"Is the shop open?\"") && json.Contains("\"when\": \"no\"") && back.Actions[0] is { Question: "Is the shop open?", Gone: true, Target: null },
            "an If that asks is written with its question and reads back the same");
        Check(Fails(() => MacroFile.Read(json.Replace("\"ask\": \"Is the shop open?\"", "\"ask\": \"Is the shop open?\", \"target\": \"Play\"")), "not both")
            && Fails(() => MacroFile.Read(json.Replace("\"when\": \"no\"", "\"when\": \"gone\"")), "takes \"yes\" or \"no\""), "an If takes a target or a question, and asks are answered yes or no");
    }
    private static bool FailsWith<T>(Action action, string message) where T : Exception
    {
        try { action(); return false; }
        catch (T ex) { return ex.Message.Contains(message); }
    }

    private static void AiUiTests()
    {
        string settingsFile = Path.Combine(Path.GetTempPath(), "keyloom-ai-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AppSettings.Load(settingsFile);
            var view = new SettingsView(settingsFile);
            var host = new Window { Content = view, Width = 760, Height = 900, Left = -20000, Top = -20000, ShowActivated = false };
            host.Show(); Pump(150);
            T Get<T>(string name) => (T)view.FindName(name);
            Check(!Get<StackPanel>("AiPanel").IsVisible, "AI is off until it is turned on");
            Get<CheckBox>("AiBox").IsChecked = true; Pump(50);
            Check(Get<StackPanel>("AiPanel").IsVisible && Get<StackPanel>("OllamaPanel").IsVisible && !Get<StackPanel>("OpenRouterPanel").IsVisible && AppSettings.Load(settingsFile).Ai,
                "turning AI on saves it and asks which AI, Ollama first");
            Get<RadioButton>("OpenRouterRadio").IsChecked = true; Pump(50);
            Get<PasswordBox>("OpenRouterKeyBox").Password = "sk-test";
            Get<ComboBox>("AiModelBox").Text = "google/some-model";
            Invoke(view, "AiField_Changed", view, new RoutedEventArgs());
            var saved = AppSettings.Load(settingsFile);
            Check(Get<StackPanel>("OpenRouterPanel").IsVisible && saved.AiProvider == AiProvider.OpenRouter && saved.AiModel == "google/some-model"
                && !File.ReadAllText(settingsFile).Contains("sk-test") && saved.AiSetup?.ApiKey == "sk-test" && Get<TextBlock>("AiProviderHint").Text.Contains("goes to OpenRouter"),
                "OpenRouter asks for a key, keeps it encrypted, and says what leaves the PC");
            Render(view, 760, 900, "ai-settings.png");
            host.Close();

            var p = Profile(MacroBehaviorType.Sequence);
            p.Actions.Add(Press("E"));
            var editor = new MacroEditorView(p);
            var editorHost = new Window { Content = editor, Width = 1100, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
            editorHost.Show(); Pump(200);
            var list = (ListBox)editor.FindName("ActionsListBox");
            Check(((MenuItem)editor.FindName("AddAiIfItem")).Visibility == Visibility.Visible, "with AI on, Add step offers an If that asks the AI");
            list.SelectedIndex = 0; Pump(50);
            Invoke(editor, "AddAiIf_Click", editor, new RoutedEventArgs()); Pump(100);
            var question = (TextBox)editor.FindName("IfQuestionTextBox");
            Check(((StackPanel)editor.FindName("IfAskPanel")).IsVisible && !((ContentControl)editor.FindName("IfTargetHost")).IsVisible && question.Text == "Is a shop window open?",
                "the If asks a question instead of looking for a target");
            question.Text = "Is the boss on the screen?";
            ((ComboBox)editor.FindName("IfAnswerBox")).SelectedIndex = 1; Pump(50);
            var read = (MacroProfile)editor.GetType().GetMethod("ReadForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(editor, [p.Snapshot()])!;
            Check(read.Actions[0] is { Block: BlockKind.If, Question: "Is the boss on the screen?", Gone: true, Target: null } && read.Actions[1].KeyName == "E",
                "the question and the answer that plays the steps are saved, and no target is needed");
            Render(editor, 1100, 760, "ai-if.png");
            editorHost.Close();
        }
        finally { File.Delete(settingsFile); AppSettings.Load(settingsFile); }
    }
}
