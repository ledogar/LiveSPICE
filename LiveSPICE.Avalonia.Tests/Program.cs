using Circuit;
using LiveSPICE.Avalonia;

static class Program
{
    private static int failures;

    public static int Main()
    {
        Run("click selects a symbol", ClickSelectsSymbol);
        Run("ctrl click toggles selection", ControlClickTogglesSelection);
        Run("drag selected symbol records one undo", DragSelectedSymbolRecordsOneUndo);
        Run("rectangle drag selects multiple elements", RectangleDragSelectsMultipleElements);
        Run("delete selection is undoable", DeleteSelectionIsUndoable);
        Run("wire clicks create undoable wire", WireClicksCreateUndoableWire);
        Run("copy paste duplicates selection", CopyPasteDuplicatesSelection);
        return failures == 0 ? 0 : 1;
    }

    private static void ClickSelectsSymbol()
    {
        TestContext context = TestContext.Load();
        Symbol symbol = context.FirstSymbol;
        Assert(context.Canvas.TestClick(Center(symbol)), "click should hit first symbol");
        Assert(context.Canvas.SelectedObjects.Single() == symbol.Component, "selected object should be symbol component");
    }

    private static void ControlClickTogglesSelection()
    {
        TestContext context = TestContext.Load();
        Symbol first = context.FirstSymbol;
        Symbol second = context.SecondSymbol;
        context.Canvas.TestClick(Center(first));
        context.Canvas.TestClick(Center(second), control: true);
        Assert(context.Canvas.SelectedObjects.Count == 2, "ctrl-click should add second selection");
        context.Canvas.TestClick(Center(first), control: true);
        Assert(context.Canvas.SelectedObjects.Count == 1, "second ctrl-click should remove existing selection");
    }

    private static void DragSelectedSymbolRecordsOneUndo()
    {
        TestContext context = TestContext.Load();
        Symbol symbol = context.FirstSymbol;
        Coord before = symbol.Position;
        Coord from = Center(symbol);
        Coord to = from + new Coord(30, 0);
        context.Canvas.TestClick(from);
        context.Canvas.TestDragSelected(from, to);
        Assert(symbol.Position == before + new Coord(30, 0), "drag should move selected symbol");
        Assert(context.Document.CanUndo, "drag should record undo");
        context.Document.Undo();
        Assert(symbol.Position == before, "undo should restore original position");
        context.Document.Redo();
        Assert(symbol.Position == before + new Coord(30, 0), "redo should reapply drag");
    }

    private static void RectangleDragSelectsMultipleElements()
    {
        TestContext context = TestContext.Load();
        Coord lower = context.Document.Schematic.LowerBound - new Coord(20, 20);
        Coord upper = context.Document.Schematic.UpperBound + new Coord(20, 20);
        context.Canvas.TestDragSelect(lower, upper);
        Assert(context.Canvas.SelectedObjects.Count >= 2, "rectangle should select multiple objects");
    }

    private static void DeleteSelectionIsUndoable()
    {
        TestContext context = TestContext.Load();
        int before = context.Document.Schematic.Elements.Count();
        context.Canvas.TestClick(Center(context.FirstSymbol));
        context.Canvas.DeleteSelection();
        Assert(context.Document.Schematic.Elements.Count() == before - 1, "delete should remove selected element");
        context.Document.Undo();
        Assert(context.Document.Schematic.Elements.Count() == before, "undo should restore deleted element");
    }

    private static void WireClicksCreateUndoableWire()
    {
        TestContext context = TestContext.Load();
        int before = context.Document.Schematic.Wires.Count();
        Coord a = new Coord(-120, -80);
        Coord b = new Coord(-60, -80);
        Assert(!context.Canvas.TestWireClick(a), "first wire click should set start point");
        Assert(context.Canvas.TestWireClick(b), "second wire click should create wire");
        Assert(context.Document.Schematic.Wires.Count() == before + 1, "wire click pair should add a wire");
        Assert(context.Document.CanUndo, "wire creation should be undoable");
        context.Document.Undo();
        Assert(context.Document.Schematic.Wires.Count() == before, "undo should remove created wire");
    }

    private static void CopyPasteDuplicatesSelection()
    {
        TestContext context = TestContext.Load();
        int before = context.Document.Schematic.Elements.Count();
        context.Canvas.TestClick(Center(context.FirstSymbol));
        string? xml = context.Canvas.CopySelectionXml();
        Assert(!string.IsNullOrWhiteSpace(xml), "copy should return serialized selection");
        Assert(context.Canvas.PasteSelectionXml(xml!), "paste should accept copied selection");
        Assert(context.Document.Schematic.Elements.Count() == before + 1, "paste should duplicate selected element");
        Assert(context.Canvas.SelectedObjects.Count == 1, "pasted element should become selected");
        context.Document.Undo();
        Assert(context.Document.Schematic.Elements.Count() == before, "undo should remove pasted element");
    }

    private static Coord Center(Element element)
    {
        return (element.LowerBound + element.UpperBound) / 2;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class TestContext
    {
        private TestContext(SchematicDocument document, SchematicCanvas canvas)
        {
            Document = document;
            Canvas = canvas;
        }

        public SchematicDocument Document { get; }

        public SchematicCanvas Canvas { get; }

        public Symbol FirstSymbol => Document.Schematic.Symbols.First();

        public Symbol SecondSymbol => Document.Schematic.Symbols.Skip(1).First();

        public static TestContext Load()
        {
            SchematicDocument document = SchematicDocument.Open("Tests/Circuits/Passive 1stOrder Highpass RC.schx");
            SchematicCanvas canvas = new SchematicCanvas { Document = document };
            return new TestContext(document, canvas);
        }
    }
}