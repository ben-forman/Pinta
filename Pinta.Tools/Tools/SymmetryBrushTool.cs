//
// SymmetryBrushTool.cs
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using Cairo;
using Gtk;
using Pinta.Core;

namespace Pinta.Tools;

/// <summary>
/// A paintbrush which mirrors every stroke across the center of the canvas.
/// </summary>
public sealed class SymmetryBrushTool : PaintBrushTool
{
	private enum MirrorMode
	{
		/// <summary>Mirror across the vertical center line (left / right).</summary>
		Vertical = 0,
		/// <summary>Mirror across the horizontal center line (top / bottom).</summary>
		Horizontal = 1,
		/// <summary>Mirror across both center lines (4-way).</summary>
		Both = 2,
	}

	// The brush draws the user's stroke onto this surface. The tool layer is
	// then rebuilt from it, together with its mirrored copies. This way every
	// brush type is mirrored exactly, without the brush having to know about it.
	private ImageSurface? stroke_surface;

	public SymmetryBrushTool (IServiceProvider services) : base (services)
	{
	}

	public override string Name => Translations.GetString ("Symmetry Brush");
	public override string Icon => Pinta.Resources.Icons.ToolSymmetryBrush;
	public override string StatusBarText => Translations.GetString ("Left click to draw with primary color, right click to draw with secondary color. Strokes are mirrored across the center of the canvas.");
	public override int Priority => 23;

	protected override string BrushTypeSettingName => SettingNames.SYMMETRY_BRUSH_BRUSH;

	protected override void OnBuildToolBar (Box tb)
	{
		base.OnBuildToolBar (tb);

		tb.Append (MirrorSeparator);
		tb.Append (MirrorLabel);
		tb.Append (MirrorComboBox);
	}

	protected override void OnMouseDown (Document document, ToolMouseEventArgs e)
	{
		// Start each new stroke with an empty stroke surface
		if (mouse_button == MouseButton.None)
			DisposeStrokeSurface ();

		base.OnMouseDown (document, e);
	}

	protected override void OnMouseUp (Document document, ToolMouseEventArgs e)
	{
		// The base class copies the tool layer (stroke + mirrored copies) onto the
		// current layer and adds a single history item for all of it.
		base.OnMouseUp (document, e);

		DisposeStrokeSurface ();
	}

	protected override RectangleI DrawStroke (Document document, BasePaintBrush brush, BrushStrokeArgs strokeArgs)
	{
		Size image_size = document.ImageSize;
		ImageSurface stroke = GetStrokeSurface (image_size);

		// Let the brush draw the real stroke
		RectangleI dirty;
		using (Context g = new (stroke)) {
			ConfigureStrokeContext (g, strokeArgs.StrokeColor);
			dirty = brush.DoMouseMove (g, stroke, strokeArgs);
		}
		stroke.MarkDirty ();

		// Rebuild the tool layer from the stroke and its mirrored copies
		Layer tool_layer = document.Layers.ToolLayer;
		tool_layer.Clear ();

		using Context tool_g = document.CreateClippedToolContext ();

		foreach ((bool flip_x, bool flip_y) in GetReflections ()) {
			tool_g.Save ();
			tool_g.Translate (flip_x ? image_size.Width : 0, flip_y ? image_size.Height : 0);
			tool_g.Scale (flip_x ? -1 : 1, flip_y ? -1 : 1);
			tool_g.SetSourceSurface (stroke, 0, 0);
			tool_g.Paint ();
			tool_g.Restore ();
		}

		// Some brushes don't report which area they changed,
		// so the whole canvas has to be treated as modified.
		if (dirty.IsEmpty)
			return new RectangleI (0, 0, image_size.Width, image_size.Height);

		RectangleI result = dirty;
		foreach ((bool flip_x, bool flip_y) in GetReflections ()) {
			result = result.Union (new RectangleI (
				flip_x ? image_size.Width - dirty.X - dirty.Width : dirty.X,
				flip_y ? image_size.Height - dirty.Y - dirty.Height : dirty.Y,
				dirty.Width,
				dirty.Height));
		}

		return result;
	}

	protected override void OnSaveSettings (ISettingsService settings)
	{
		base.OnSaveSettings (settings);

		if (mirror_combo_box is not null)
			settings.PutSetting (SettingNames.SYMMETRY_BRUSH_MODE, mirror_combo_box.ComboBox.Active);
	}

	/// <summary>
	/// The copies of the stroke to draw (including the original, unflipped one),
	/// described by whether each is flipped horizontally and / or vertically.
	/// </summary>
	private IEnumerable<(bool flip_x, bool flip_y)> GetReflections ()
	{
		MirrorMode mode = CurrentMirrorMode;

		yield return (false, false);

		if (mode is MirrorMode.Vertical or MirrorMode.Both)
			yield return (true, false);

		if (mode is MirrorMode.Horizontal or MirrorMode.Both)
			yield return (false, true);

		if (mode is MirrorMode.Both)
			yield return (true, true);
	}

	private MirrorMode CurrentMirrorMode => MirrorComboBox.ComboBox.Active switch {
		1 => MirrorMode.Horizontal,
		2 => MirrorMode.Both,
		_ => MirrorMode.Vertical,
	};

	private ImageSurface GetStrokeSurface (Size image_size)
	{
		if (stroke_surface is not null && (stroke_surface.Width != image_size.Width || stroke_surface.Height != image_size.Height))
			DisposeStrokeSurface ();

		return stroke_surface ??= CairoExtensions.CreateImageSurface (Format.Argb32, image_size.Width, image_size.Height);
	}

	private void DisposeStrokeSurface ()
	{
		stroke_surface?.Dispose ();
		stroke_surface = null;
	}

	private Label? mirror_label;
	private ToolBarComboBox? mirror_combo_box;
	private Gtk.Separator? mirror_separator;

	private Gtk.Separator MirrorSeparator => mirror_separator ??= GtkExtensions.CreateToolBarSeparator ();
	private Label MirrorLabel => mirror_label ??= Label.New (string.Format (" {0}:  ", Translations.GetString ("Mirror")));

	private ToolBarComboBox MirrorComboBox {
		get {
			if (mirror_combo_box is null) {
				int saved_mode = Math.Clamp (Settings.GetSetting (SettingNames.SYMMETRY_BRUSH_MODE, 0), 0, 2);

				// The order must match the values of MirrorMode
				mirror_combo_box = ToolBarComboBox.New (
					130,
					saved_mode,
					false,
					Translations.GetString ("Vertical"),
					Translations.GetString ("Horizontal"),
					Translations.GetString ("Both (4-way)"));

				mirror_combo_box.TooltipText = Translations.GetString ("Vertical mirrors left and right, horizontal mirrors top and bottom, and both mirrors in all four quadrants.");
			}

			return mirror_combo_box;
		}
	}
}
