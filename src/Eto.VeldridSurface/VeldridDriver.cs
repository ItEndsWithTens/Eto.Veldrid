﻿using Eto.Drawing;
using Eto.Forms;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;
using Veldrid.SPIRV;

namespace PlaceholderName
{
	public struct VertexPositionColor
	{
		public static uint SizeInBytes = (uint)Marshal.SizeOf(typeof(VertexPositionColor));

		Vector3 Position;
		RgbaFloat Color;

		public VertexPositionColor(Vector3 position, RgbaFloat color)
		{
			Position = position;
			Color = color;
		}
	}

	/// <summary>
	/// A class that controls rendering to a VeldridSurface.
	/// </summary>
	/// <remarks>
	/// VeldridSurface is only a basic control that lets you render to the screen
	/// using Veldrid. How exactly to do that is up to you; this driver class is
	/// only one possible approach, and in all likelihood not the most efficient.
	/// </remarks>
	public class VeldridDriver
	{
		public VeldridSurface Surface;

		public UITimer Clock = new UITimer();

		public delegate void updateHost();
		public updateHost updateHostFunc { get; set; }

		public bool ok;
		bool immediateMode;
		public bool lockedViewport;
		public bool savedLocation_valid;
		PointF savedLocation;

		VertexPositionColor[] polyArray;
		ushort[] polyFirst;
		ushort[] polyIndices;
		int poly_vbo_size;

		VertexPositionColor[] lineArray;
		ushort[] lineFirst;
		ushort[] lineIndices;
		int line_vbo_size;

		VertexPositionColor[] gridArray;
		ushort[] gridIndices;
		int grid_vbo_size;

		VertexPositionColor[] axesArray;
		ushort[] axesIndices;
		int axes_vbo_size;

		public OVPSettings ovpSettings;

		CommandList CommandList;
		DeviceBuffer GridVertexBuffer;
		DeviceBuffer GridIndexBuffer;
		DeviceBuffer AxesVertexBuffer;
		DeviceBuffer AxesIndexBuffer;

		DeviceBuffer LinesVertexBuffer;
		DeviceBuffer LinesIndexBuffer;

		DeviceBuffer PolysVertexBuffer;
		DeviceBuffer PolysIndexBuffer;

		Shader VertexShader;
		Shader FragmentShader;
		Pipeline LinePipeline;
		Pipeline LinesPipeline;
		Pipeline FilledPipeline;

		Matrix4x4 ModelMatrix = Matrix4x4.Identity;
		DeviceBuffer ModelBuffer;
		ResourceSet ModelMatrixSet;

		Matrix4x4 ViewMatrix;
		DeviceBuffer ViewBuffer;
		ResourceSet ViewMatrixSet;

		private bool Ready = false;

		public VeldridDriver(ref OVPSettings svpSettings, ref VeldridSurface surface)
		{
			try
			{
				ovpSettings = svpSettings;
				Surface = surface;
				Surface.MouseDown += downHandler;
				Surface.MouseMove += dragHandler;
				Surface.MouseUp += upHandler;
				Surface.MouseWheel += zoomHandler;
				Surface.GotFocus += addKeyHandler;
				// MouseHover += addKeyHandler;
				Surface.LostFocus += removeKeyHandler;
			}
			catch (Exception)
			{

			}
			Clock.Interval = 1.0f / 60.0f;
			Clock.Elapsed += Clock_Elapsed;

			Surface.Resize += (sender, e) => updateViewport();
		}

		private void Clock_Elapsed(object sender, EventArgs e)
		{
			Draw();
		}

		private DateTime CurrentTime;
		private DateTime PreviousTime = DateTime.Now;

		float axisZ;
		float gridZ;

		// Use for drag handling.
		bool dragging;
		float x_orig;
		float y_orig;

		ContextMenu menu;

		public void setContextMenu(ref ContextMenu menu_)
		{
			menu = menu_;
		}

		public void changeSettingsRef(ref OVPSettings newSettings)
		{
			ovpSettings = newSettings;
			updateViewport();
		}

		Point WorldToScreen(float x, float y)
		{
			return new Point((int)((x - ovpSettings.cameraPosition.X / (ovpSettings.zoomFactor * ovpSettings.base_zoom)) + Surface.Width / 2),
					(int)((y - ovpSettings.cameraPosition.Y / (ovpSettings.zoomFactor * ovpSettings.base_zoom)) + Surface.Height / 2));
		}

		Point WorldToScreen(PointF pt)
		{
			return WorldToScreen(pt.X, pt.Y);
		}

		Size WorldToScreen(SizeF pt)
		{
			Point pt1 = WorldToScreen(0, 0);
			Point pt2 = WorldToScreen(pt.Width, pt.Height);
			return new Size(pt2.X - pt1.X, pt2.Y - pt1.Y);
		}

		PointF ScreenToWorld(int x, int y)
		{
			return new PointF((float)(x - Surface.Width / 2) * (ovpSettings.zoomFactor * ovpSettings.base_zoom) + ovpSettings.cameraPosition.X,
					 (float)(y - Surface.Height / 2) * (ovpSettings.zoomFactor * ovpSettings.base_zoom) + ovpSettings.cameraPosition.Y);
		}

		PointF ScreenToWorld(Point pt)
		{
			return ScreenToWorld(pt.X, pt.Y);
		}

		RectangleF getViewPort()
		{
			PointF bl = ScreenToWorld(Surface.Location.X - Surface.Width / 2, Surface.Location.Y - Surface.Height / 2);
			PointF tr = ScreenToWorld(Surface.Location.X + Surface.Width / 2, Surface.Location.Y + Surface.Height / 2);
			return new RectangleF(bl.X, bl.Y, tr.X - bl.X, tr.Y - bl.Y);
		}

		void setViewPort(float x1, float y1, float x2, float y2)
		{
			float h = Math.Abs(y1 - y2);
			float w = Math.Abs(x1 - x2);
			ovpSettings.cameraPosition = new PointF((x1 + x2) / 2, (y1 + y2) / 2);
			if ((Surface.Height != 0) && (Surface.Width != 0))
			{
				ovpSettings.zoomFactor = Math.Max(h / (float)(Surface.Height), w / (float)(Surface.Width));
			}
			else
			{
				ovpSettings.zoomFactor = 1;
			}
		}

		void downHandler(object sender, MouseEventArgs e)
		{
			if (e.Buttons == MouseButtons.Primary)
			{
				if (!dragging && !lockedViewport) // might not be needed, but seemed like a safe approach to avoid re-setting these in a drag event.
				{
					x_orig = e.Location.X;
					y_orig = e.Location.Y;
					dragging = true;
				}
			}
			//e.Handled = true;
		}

		public void saveLocation()
		{
			savedLocation = new PointF(ovpSettings.cameraPosition.X, ovpSettings.cameraPosition.Y);
			savedLocation_valid = true;
		}

		public void zoomExtents()
		{
			getExtents();

			if (((ovpSettings.polyList.Count == 0) && (ovpSettings.lineList.Count == 0)) ||
				((ovpSettings.minX == 0) && (ovpSettings.maxX == 0)) ||
				((ovpSettings.minY == 0) && (ovpSettings.maxY == 0)))
			{
				reset();
				return;
			}

			// Locate camera at center of the polygon field.
			float dX = ovpSettings.maxX - ovpSettings.minX;
			float dY = ovpSettings.maxY - ovpSettings.minY;
			float cX = (dX / 2.0f) + ovpSettings.minX;
			float cY = (dY / 2.0f) + ovpSettings.minY;

			// Now need to get the zoom level organized.
			float zoomLevel_x = dX / Surface.Width;
			float zoomLevel_y = dY / Surface.Height;

			if (zoomLevel_x > zoomLevel_y)
			{
				ovpSettings.zoomFactor = zoomLevel_x / ovpSettings.base_zoom;
			}
			else
			{
				ovpSettings.zoomFactor = zoomLevel_y / ovpSettings.base_zoom;
			}

			goToLocation(cX, cY);
		}

		public void loadLocation()
		{
			if (savedLocation_valid)
			{
				ovpSettings.cameraPosition = new PointF(savedLocation.X, savedLocation.Y);
				updateViewport();
			}
		}

		public void goToLocation(float x, float y)
		{
			ovpSettings.cameraPosition = new PointF(x, y);
			updateViewport();
		}

		void dragHandler(object sender, MouseEventArgs e)
		{
			if (lockedViewport)
			{
				return;
			}
			if (e.Buttons == MouseButtons.Primary)
			{
				object locking = new object();
				lock (locking)
				{
					// Scaling factor is arbitrary - just based on testing to avoid insane panning speeds.
					float new_X = (ovpSettings.cameraPosition.X - (((float)e.Location.X - x_orig) / 100.0f));
					float new_Y = (ovpSettings.cameraPosition.Y + (((float)e.Location.Y - y_orig) / 100.0f));
					ovpSettings.cameraPosition = new PointF(new_X, new_Y);
				}
			}
			updateViewport();
			//e.Handled = true;
		}

		public void freeze_thaw()
		{
			lockedViewport = !lockedViewport;
		}

		void upHandler(object sender, MouseEventArgs e)
		{
			if (lockedViewport)
			{
				return;
			}
			if (e.Buttons == MouseButtons.Primary)
			{
				dragging = false;
			}
			if (e.Buttons == MouseButtons.Alternate)
			{
				if (menu != null)
				{
					menu.Show(Surface);
				}
			}
			//e.Handled = true
		}

		public void zoomIn(float delta)
		{
			if (lockedViewport)
			{
				return;
			}
			ovpSettings.zoomFactor += (ovpSettings.zoomStep * 0.01f * delta);
		}

		public void zoomOut(float delta)
		{
			if (lockedViewport)
			{
				return;
			}
			ovpSettings.zoomFactor -= (ovpSettings.zoomStep * 0.01f * delta);
			if (ovpSettings.zoomFactor < 0.0001)
			{
				ovpSettings.zoomFactor = 0.0001f; // avoid any chance of getting to zero.
			}
		}

		void panVertical(float delta)
		{
			if (lockedViewport)
			{
				return;
			}
			ovpSettings.cameraPosition.Y += delta / 10;
		}

		void panHorizontal(float delta)
		{
			if (lockedViewport)
			{
				return;
			}
			ovpSettings.cameraPosition.X += delta / 10;
		}

		void addKeyHandler(object sender, EventArgs e)
		{
			Surface.KeyDown += keyHandler;
		}

		void removeKeyHandler(object sender, EventArgs e)
		{
			Surface.KeyDown -= keyHandler;
		}

		public void reset()
		{
			if (lockedViewport)
			{
				return;
			}
			ovpSettings.cameraPosition = new PointF(ovpSettings.default_cameraPosition.X, ovpSettings.default_cameraPosition.Y);
			ovpSettings.zoomFactor = 1.0f;
		}

		void keyHandler(object sender, KeyEventArgs e)
		{
			if (lockedViewport)
			{
				if (e.Key != Keys.F)
				{
					return;
				}
				lockedViewport = false;
				return;
			}

			if (e.Key == Keys.F)
			{
				lockedViewport = true;
				return;
			}

			if (e.Key == Keys.R)
			{
				reset();
			}

			float stepping = 10.0f * ovpSettings.zoomFactor;

			bool doUpdate = true;
			if (e.Key == Keys.A)
			{
				panHorizontal(-stepping);
			}
			if (e.Key == Keys.D)
			{
				panHorizontal(stepping);
			}
			if (e.Key == Keys.W)
			{
				panVertical(stepping);
			}
			if (e.Key == Keys.S)
			{
				panVertical(-stepping);
			}
			if (e.Key == Keys.N)
			{
				zoomOut(-1);
			}
			if (e.Key == Keys.M)
			{
				zoomIn(-1);
			}

			if (e.Key == Keys.X)
			{
				zoomExtents();
				doUpdate = false; // update performed in extents
			}

			if (doUpdate)
			{
				updateViewport();
			}
			e.Handled = true;
		}

		void zoomHandler(object sender, MouseEventArgs e)
		{
			if (lockedViewport)
			{
				return;
			}

			float wheelZoom = e.Delta.Height; // SystemInformation.MouseWheelScrollLines;
			if (wheelZoom > 0)
			{
				zoomIn(wheelZoom);
			}
			if (wheelZoom < 0)
			{
				zoomOut(-wheelZoom);
			}
			updateViewport();
			//e.Handled = true;
		}

		void getExtents()
		{
			float minX = 0;
			float maxX = 0;
			float minY = 0, maxY = 0;

			if ((ovpSettings.polyList.Count == 0) && (ovpSettings.lineList.Count == 0))
			{
				ovpSettings.minX = 0;
				ovpSettings.maxX = 0;
				ovpSettings.minY = 0;
				ovpSettings.maxY = 0;
				return;
			}

			if (ovpSettings.polyList.Count != 0)
			{
				minX = ovpSettings.polyList[0].poly[0].X;
				maxX = ovpSettings.polyList[0].poly[0].X;
				minY = ovpSettings.polyList[0].poly[0].Y;
				maxY = ovpSettings.polyList[0].poly[0].Y;
				for (int poly = 0; poly < ovpSettings.polyList.Count; poly++)
				{
					float tMinX = ovpSettings.polyList[poly].poly.Min(p => p.X);
					if (tMinX < minX)
					{
						minX = tMinX;
					}
					float tMaxX = ovpSettings.polyList[poly].poly.Max(p => p.X);
					if (tMaxX > maxX)
					{
						maxX = tMaxX;
					}
					float tMinY = ovpSettings.polyList[poly].poly.Min(p => p.Y);
					if (tMinY < minY)
					{
						minY = tMinY;
					}
					float tMaxY = ovpSettings.polyList[poly].poly.Max(p => p.Y);
					if (tMaxY > maxY)
					{
						maxY = tMaxY;
					}
				}
			}

			if (ovpSettings.lineList.Count != 0)
			{
				for (int line = 0; line < ovpSettings.lineList.Count; line++)
				{
					float tMinX = ovpSettings.lineList[line].poly.Min(p => p.X);
					if (tMinX < minX)
					{
						minX = tMinX;
					}
					float tMaxX = ovpSettings.lineList[line].poly.Max(p => p.X);
					if (tMaxX > maxX)
					{
						maxX = tMaxX;
					}
					float tMinY = ovpSettings.lineList[line].poly.Min(p => p.Y);
					if (tMinY < minY)
					{
						minY = tMinY;
					}
					float tMaxY = ovpSettings.lineList[line].poly.Max(p => p.Y);
					if (tMaxY > maxY)
					{
						maxY = tMaxY;
					}
				}
			}

			ovpSettings.minX = minX;
			ovpSettings.maxX = maxX;
			ovpSettings.minY = minY;
			ovpSettings.maxY = maxY;
		}

		void drawPolygons()
		{
			try
			{
				List<VertexPositionColor> polyList = new List<VertexPositionColor>();

				// Carve our Z-space up to stack polygons
				float polyZStep = 1.0f / ovpSettings.polyList.Count();

				// Create our first and count arrays for the vertex indices, to enable polygon separation when rendering.
				polyFirst = new ushort[ovpSettings.polyList.Count()];
				polyIndices = new ushort[ovpSettings.polyList.Count()];
				int counter = 0; // vertex count that will be used to define 'first' index for each polygon.
				int previouscounter = 0; // will be used to derive the number of vertices in each polygon.

				for (int poly = 0; poly < ovpSettings.polyList.Count(); poly++)
				{
					float alpha = ovpSettings.polyList[poly].alpha;
					float polyZ = poly * polyZStep;
					polyFirst[poly] = (ushort)counter;
					previouscounter = counter;
					if ((ovpSettings.enableFilledPolys) && (!ovpSettings.drawnPoly[poly]))
					{
						polyList.Add(new VertexPositionColor(new Vector3(ovpSettings.polyList[poly].poly[0].X, ovpSettings.polyList[poly].poly[0].Y, polyZ), new RgbaFloat(ovpSettings.polyList[poly].color.R, ovpSettings.polyList[poly].color.G, ovpSettings.polyList[poly].color.B, alpha)));
						polyList.Add(new VertexPositionColor(new Vector3(ovpSettings.polyList[poly].poly[1].X, ovpSettings.polyList[poly].poly[1].Y, polyZ), new RgbaFloat(ovpSettings.polyList[poly].color.R, ovpSettings.polyList[poly].color.G, ovpSettings.polyList[poly].color.B, alpha)));
						polyList.Add(new VertexPositionColor(new Vector3(ovpSettings.polyList[poly].poly[2].X, ovpSettings.polyList[poly].poly[2].Y, polyZ), new RgbaFloat(ovpSettings.polyList[poly].color.R, ovpSettings.polyList[poly].color.G, ovpSettings.polyList[poly].color.B, alpha)));
						counter += 3;
						polyIndices[poly] = 3;
					}
					else
					{
						for (int pt = 0; pt < ovpSettings.polyList[poly].poly.Length - 1; pt++)
						{
							polyList.Add(new VertexPositionColor(new Vector3(ovpSettings.polyList[poly].poly[pt].X, ovpSettings.polyList[poly].poly[pt].Y, polyZ), new RgbaFloat(ovpSettings.polyList[poly].color.R, ovpSettings.polyList[poly].color.G, ovpSettings.polyList[poly].color.B, alpha)));
							counter++;
							polyList.Add(new VertexPositionColor(new Vector3(ovpSettings.polyList[poly].poly[pt + 1].X, ovpSettings.polyList[poly].poly[pt + 1].Y, polyZ), new RgbaFloat(ovpSettings.polyList[poly].color.R, ovpSettings.polyList[poly].color.G, ovpSettings.polyList[poly].color.B, alpha)));
							counter++;
						}
						polyIndices[poly] = (ushort)(counter - previouscounter); // set our vertex count for the polygon.
					}
				}

				polyArray = polyList.ToArray();

				PolysVertexBuffer?.Dispose();
				PolysIndexBuffer?.Dispose();

				ResourceFactory factory = Surface.GraphicsDevice.ResourceFactory;

				PolysVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)polyArray.Length * VertexPositionColor.SizeInBytes, BufferUsage.VertexBuffer));
				PolysIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)polyIndices.Length * sizeof(ushort), BufferUsage.IndexBuffer));

				Surface.GraphicsDevice.UpdateBuffer(PolysVertexBuffer, 0, polyArray);
				Surface.GraphicsDevice.UpdateBuffer(PolysIndexBuffer, 0, polyIndices);

			}
			catch (Exception)
			{
				// Can ignore - not critical.
			}
		}

		void drawLines()
		{
			try
			{
				List<VertexPositionColor> polyList = new List<VertexPositionColor>();

				// Carve our Z-space up to stack polygons
				float polyZStep = 1.0f / ovpSettings.lineList.Count();

				// Create our first and count arrays for the vertex indices, to enable polygon separation when rendering.
				int tmp = ovpSettings.lineList.Count();
				lineFirst = new ushort[tmp];
				lineIndices = new ushort[tmp];
				int counter = 0; // vertex count that will be used to define 'first' index for each polygon.
				int previouscounter = 0; // will be used to derive the number of vertices in each polygon.

				for (int poly = 0; poly < ovpSettings.lineList.Count(); poly++)
				{
					float alpha = ovpSettings.lineList[poly].alpha;
					float polyZ = poly * polyZStep;
					lineFirst[poly] = (ushort)counter;
					previouscounter = counter;
					for (int pt = 0; pt < ovpSettings.lineList[poly].poly.Length - 1; pt++)
					{
						polyList.Add(new VertexPositionColor(new Vector3(ovpSettings.lineList[poly].poly[pt].X, ovpSettings.lineList[poly].poly[pt].Y, polyZ), new RgbaFloat(ovpSettings.lineList[poly].color.R, ovpSettings.lineList[poly].color.G, ovpSettings.lineList[poly].color.B, alpha)));
						counter++;
						polyList.Add(new VertexPositionColor(new Vector3(ovpSettings.lineList[poly].poly[pt + 1].X, ovpSettings.lineList[poly].poly[pt + 1].Y, polyZ), new RgbaFloat(ovpSettings.lineList[poly].color.R, ovpSettings.lineList[poly].color.G, ovpSettings.lineList[poly].color.B, alpha)));
						counter++;
					}
					lineIndices[poly] = (ushort)(counter - previouscounter); // set our vertex count for the polygon.
				}

				lineArray = polyList.ToArray();

				LinesVertexBuffer?.Dispose();
				LinesIndexBuffer?.Dispose();

				ResourceFactory factory = Surface.GraphicsDevice.ResourceFactory;

				LinesVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)lineArray.Length * VertexPositionColor.SizeInBytes, BufferUsage.VertexBuffer));
				LinesIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)lineIndices.Length * sizeof(ushort), BufferUsage.IndexBuffer));

				Surface.GraphicsDevice.UpdateBuffer(LinesVertexBuffer, 0, lineArray);
				Surface.GraphicsDevice.UpdateBuffer(LinesIndexBuffer, 0, lineIndices);
			}
			catch (Exception)
			{
				// Can ignore - not critical.
			}
		}

		void drawGrid()
		{
			if (ovpSettings.showGrid)
			{
				float spacing = ovpSettings.gridSpacing;
				if (ovpSettings.dynamicGrid)
				{
					while (WorldToScreen(new SizeF(spacing, 0.0f)).Width > 12.0f)
						spacing /= 10.0f;

					while (WorldToScreen(new SizeF(spacing, 0.0f)).Width < 4.0f)
						spacing *= 10.0f;
				}

				List<VertexPositionColor> grid = new List<VertexPositionColor>();

				if (WorldToScreen(new SizeF(spacing, 0.0f)).Width >= 4.0f)
				{
					int k = 0;
					for (float i = 0; i > -(Surface.Width * (ovpSettings.zoomFactor * ovpSettings.base_zoom)) + ovpSettings.cameraPosition.X; i -= spacing)
					{
						float r = 0.0f;
						float g = 0.0f;
						float b = 0.0f;
						if (k <= 9)
						{
							r = ovpSettings.minorGridColor.R;
							g = ovpSettings.minorGridColor.G;
							b = ovpSettings.minorGridColor.B;
						}
						if (k == 10)
						{
							r = ovpSettings.majorGridColor.R;
							g = ovpSettings.majorGridColor.G;
							b = ovpSettings.majorGridColor.B;
							k = 0;
						}
						k++;
						grid.Add(new VertexPositionColor(new Vector3(i, ovpSettings.cameraPosition.Y + (ovpSettings.zoomFactor * ovpSettings.base_zoom) * Surface.Height, gridZ), new RgbaFloat(r, g, b, 1.0f)));
						grid.Add(new VertexPositionColor(new Vector3(i, ovpSettings.cameraPosition.Y + (ovpSettings.zoomFactor * ovpSettings.base_zoom) * -Surface.Height, gridZ), new RgbaFloat(r, g, b, 1.0f)));
					}
					k = 0;
					for (float i = 0; i < (Surface.Width * (ovpSettings.zoomFactor * ovpSettings.base_zoom)) + ovpSettings.cameraPosition.X; i += spacing)
					{
						float r = 0.0f;
						float g = 0.0f;
						float b = 0.0f;
						if (k <= 9)
						{
							r = ovpSettings.minorGridColor.R;
							g = ovpSettings.minorGridColor.G;
							b = ovpSettings.minorGridColor.B;
						}
						if (k == 10)
						{
							r = ovpSettings.majorGridColor.R;
							g = ovpSettings.majorGridColor.G;
							b = ovpSettings.majorGridColor.B;
							k = 0;
						}
						k++;
						grid.Add(new VertexPositionColor(new Vector3(i, ovpSettings.cameraPosition.Y + (ovpSettings.zoomFactor * ovpSettings.base_zoom) * Surface.Height, gridZ), new RgbaFloat(r, g, b, 1.0f)));
						grid.Add(new VertexPositionColor(new Vector3(i, ovpSettings.cameraPosition.Y + (ovpSettings.zoomFactor * ovpSettings.base_zoom) * -Surface.Height, gridZ), new RgbaFloat(r, g, b, 1.0f)));
					}
					k = 0;
					for (float i = 0; i > -(Surface.Height * (ovpSettings.zoomFactor * ovpSettings.base_zoom)) + ovpSettings.cameraPosition.Y; i -= spacing)
					{
						float r = 0.0f;
						float g = 0.0f;
						float b = 0.0f;
						if (k <= 9)
						{
							r = ovpSettings.minorGridColor.R;
							g = ovpSettings.minorGridColor.G;
							b = ovpSettings.minorGridColor.B;
						}
						if (k == 10)
						{
							r = ovpSettings.majorGridColor.R;
							g = ovpSettings.majorGridColor.G;
							b = ovpSettings.majorGridColor.B;
							k = 0;
						}
						k++;
						grid.Add(new VertexPositionColor(new Vector3(ovpSettings.cameraPosition.X + (ovpSettings.zoomFactor * ovpSettings.base_zoom) * Surface.Width, i, gridZ), new RgbaFloat(r, g, b, 1.0f)));
						grid.Add(new VertexPositionColor(new Vector3(ovpSettings.cameraPosition.X + (ovpSettings.zoomFactor * ovpSettings.base_zoom) * -Surface.Width, i, gridZ), new RgbaFloat(r, g, b, 1.0f)));
					}
					k = 0;
					for (float i = 0; i < (Surface.Height * (ovpSettings.zoomFactor * ovpSettings.base_zoom)) + ovpSettings.cameraPosition.Y; i += spacing)
					{
						float r = 0.0f;
						float g = 0.0f;
						float b = 0.0f;
						if (k <= 9)
						{
							r = ovpSettings.minorGridColor.R;
							g = ovpSettings.minorGridColor.G;
							b = ovpSettings.minorGridColor.B;
						}
						if (k == 10)
						{
							r = ovpSettings.majorGridColor.R;
							g = ovpSettings.majorGridColor.G;
							b = ovpSettings.majorGridColor.B;
							k = 0;
						}
						k++;
						grid.Add(new VertexPositionColor(new Vector3(ovpSettings.cameraPosition.X + (ovpSettings.zoomFactor * ovpSettings.base_zoom) * Surface.Width, i, gridZ), new RgbaFloat(r, g, b, 1.0f)));
						grid.Add(new VertexPositionColor(new Vector3(ovpSettings.cameraPosition.X + (ovpSettings.zoomFactor * ovpSettings.base_zoom) * -Surface.Width, i, gridZ), new RgbaFloat(r, g, b, 1.0f)));
					}
					gridArray = grid.ToArray();
					gridIndices = new ushort[gridArray.Length];
					for (ushort i = 0; i < gridIndices.Length; i++)
					{
						gridIndices[i] = i;
					}
				}

				GridVertexBuffer?.Dispose();
				GridIndexBuffer?.Dispose();

				ResourceFactory factory = Surface.GraphicsDevice.ResourceFactory;

				GridVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)gridArray.Length * VertexPositionColor.SizeInBytes, BufferUsage.VertexBuffer));
				GridIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)gridIndices.Length * sizeof(ushort), BufferUsage.IndexBuffer));

				Surface.GraphicsDevice.UpdateBuffer(GridVertexBuffer, 0, gridArray);
				Surface.GraphicsDevice.UpdateBuffer(GridIndexBuffer, 0, gridIndices);
			}
		}

		void drawAxes()
		{
			if (ovpSettings.showAxes)
			{
				axesArray = new VertexPositionColor[4];
				axesArray[0] = new VertexPositionColor(new Vector3(0.0f, ovpSettings.cameraPosition.Y + Surface.Height * (ovpSettings.zoomFactor * ovpSettings.base_zoom), axisZ), new RgbaFloat(ovpSettings.axisColor.R, ovpSettings.axisColor.G, ovpSettings.axisColor.B, 1.0f));
				axesArray[1] = new VertexPositionColor(new Vector3(0.0f, ovpSettings.cameraPosition.Y - Surface.Height * (ovpSettings.zoomFactor * ovpSettings.base_zoom), axisZ), new RgbaFloat(ovpSettings.axisColor.R, ovpSettings.axisColor.G, ovpSettings.axisColor.B, 1.0f));
				axesArray[2] = new VertexPositionColor(new Vector3(ovpSettings.cameraPosition.X + Surface.Width * (ovpSettings.zoomFactor * ovpSettings.base_zoom), 0.0f, axisZ), new RgbaFloat(ovpSettings.axisColor.R, ovpSettings.axisColor.G, ovpSettings.axisColor.B, 1.0f));
				axesArray[3] = new VertexPositionColor(new Vector3(ovpSettings.cameraPosition.X - Surface.Width * (ovpSettings.zoomFactor * ovpSettings.base_zoom), 0.0f, axisZ), new RgbaFloat(ovpSettings.axisColor.R, ovpSettings.axisColor.G, ovpSettings.axisColor.B, 1.0f));

				axesIndices = new ushort[4] { 0, 1, 2, 3 };

				AxesVertexBuffer?.Dispose();
				AxesIndexBuffer?.Dispose();

				ResourceFactory factory = Surface.GraphicsDevice.ResourceFactory;

				AxesVertexBuffer = factory.CreateBuffer(new BufferDescription((uint)axesArray.Length * VertexPositionColor.SizeInBytes, BufferUsage.VertexBuffer));
				AxesIndexBuffer = factory.CreateBuffer(new BufferDescription((uint)axesIndices.Length * sizeof(ushort), BufferUsage.IndexBuffer));

				Surface.GraphicsDevice.UpdateBuffer(AxesVertexBuffer, 0, axesArray);
				Surface.GraphicsDevice.UpdateBuffer(AxesIndexBuffer, 0, axesIndices);
			}

		}

		public void updateViewport()
		{
			drawAxes();
			drawGrid();
			drawLines();
			drawPolygons();
			Draw();
		}

		public void Draw()
		{
			if (!Ready)
			{
				return;
			}

			CommandList.Begin();

			ModelMatrix *= Matrix4x4.CreateFromAxisAngle(
				new Vector3(0, 0, 1),0);
			CommandList.UpdateBuffer(ModelBuffer, 0, ModelMatrix);

			float zoom = ovpSettings.zoomFactor * ovpSettings.base_zoom;

			float left = ovpSettings.cameraPosition.X - (Surface.Width / 2) * zoom;
			float right = ovpSettings.cameraPosition.X + (Surface.Width / 2) * zoom;
			float bottom = ovpSettings.cameraPosition.Y + (Surface.Height / 2) * zoom;
			float top = ovpSettings.cameraPosition.Y - (Surface.Height / 2) * zoom;

			ViewMatrix = Matrix4x4.CreateOrthographicOffCenter(left, right, bottom, top, 0.0f, 1.0f);
			CommandList.UpdateBuffer(ViewBuffer, 0, ViewMatrix);

			CommandList.SetFramebuffer(Surface.Swapchain.Framebuffer);

			// These commands differ from the stock Veldrid "Getting Started"
			// tutorial in two ways. First, the viewport is cleared to pink
			// instead of black so as to more easily distinguish between errors
			// in creating a graphics context and errors drawing vertices within
			// said context. Second, this project creates its swapchain with a
			// depth buffer, and that buffer needs to be reset at the start of
			// each frame.
			CommandList.ClearColorTarget(0, RgbaFloat.Pink);
			CommandList.ClearDepthStencil(1.0f);

			CommandList.SetVertexBuffer(0, GridVertexBuffer);
			CommandList.SetIndexBuffer(GridIndexBuffer, IndexFormat.UInt16);
			CommandList.SetPipeline(LinePipeline);
			CommandList.SetGraphicsResourceSet(0, ViewMatrixSet);
			CommandList.SetGraphicsResourceSet(1, ModelMatrixSet);

			CommandList.DrawIndexed(
				indexCount: (uint)gridIndices.Length,
				instanceCount: 1,
				indexStart: 0,
				vertexOffset: 0,
				instanceStart: 0);

			CommandList.SetVertexBuffer(0, AxesVertexBuffer);
			CommandList.SetIndexBuffer(AxesIndexBuffer, IndexFormat.UInt16);
			CommandList.SetPipeline(LinePipeline);
			CommandList.SetGraphicsResourceSet(0, ViewMatrixSet);
			CommandList.SetGraphicsResourceSet(1, ModelMatrixSet);

			CommandList.DrawIndexed(
				indexCount: (uint)axesIndices.Length,
				instanceCount: 1,
				indexStart: 0,
				vertexOffset: 0,
				instanceStart: 0);

			CommandList.SetVertexBuffer(0, LinesVertexBuffer);
			CommandList.SetIndexBuffer(LinesIndexBuffer, IndexFormat.UInt16);
			CommandList.SetPipeline(LinesPipeline);
			CommandList.SetGraphicsResourceSet(0, ViewMatrixSet);
			CommandList.SetGraphicsResourceSet(1, ModelMatrixSet);

			//for (int l = 0; l < lineIndices.Length; l++)
			{
				CommandList.DrawIndexed(
					indexCount: (uint)lineIndices.Length,
					instanceCount: 1,
					indexStart: (uint)lineFirst[0],
					vertexOffset: 0,
					instanceStart: 0);
			}
			/*
			CommandList.SetVertexBuffer(0, PolysVertexBuffer);
			CommandList.SetIndexBuffer(PolysIndexBuffer, IndexFormat.UInt16);
			CommandList.SetPipeline(FilledPipeline);
			CommandList.SetGraphicsResourceSet(0, ViewMatrixSet);
			CommandList.SetGraphicsResourceSet(1, ModelMatrixSet);

			CommandList.DrawIndexed(
				indexCount: (uint)polyIndices.Length,
				instanceCount: 1,
				indexStart: 0,
				vertexOffset: 0,
				instanceStart: 0);
				*/
			CommandList.End();

			Surface.GraphicsDevice.SubmitCommands(CommandList);
			Surface.GraphicsDevice.SwapBuffers(Surface.Swapchain);
		}

		public void SetUpVeldrid()
		{
			CreateResources();

			Ready = true;
		}

		private void CreateResources()
		{
			ResourceFactory factory = Surface.GraphicsDevice.ResourceFactory;

			ResourceLayout viewMatrixLayout = factory.CreateResourceLayout(
				new ResourceLayoutDescription(
					new ResourceLayoutElementDescription(
						"ViewMatrix",
						ResourceKind.UniformBuffer,
						ShaderStages.Vertex)));

			ViewBuffer = factory.CreateBuffer(
				new BufferDescription(64, BufferUsage.UniformBuffer));

			ViewMatrixSet = factory.CreateResourceSet(new ResourceSetDescription(
				viewMatrixLayout, ViewBuffer));

			ResourceLayout modelMatrixLayout = factory.CreateResourceLayout(
				new ResourceLayoutDescription(
					new ResourceLayoutElementDescription(
						"ModelMatrix",
						ResourceKind.UniformBuffer,
						ShaderStages.Vertex)));

			ModelBuffer = factory.CreateBuffer(
				new BufferDescription(64, BufferUsage.UniformBuffer));

			ModelMatrixSet = factory.CreateResourceSet(new ResourceSetDescription(
				modelMatrixLayout, ModelBuffer));

			drawGrid();


			/*
			VertexPositionColor[] quad2Vertices =
{
				new VertexPositionColor(new Vector3(-.85f, -.85f, 0.1f), RgbaFloat.Yellow),
				new VertexPositionColor(new Vector3(.85f, -.85f, 0.1f), RgbaFloat.Blue),
				new VertexPositionColor(new Vector3(-.85f, .85f, 0.1f), RgbaFloat.Green),
				new VertexPositionColor(new Vector3(.85f, .85f, 0.1f), RgbaFloat.Red)
			};

			ushort[] quad2Indices = { 0, 1, 2, 3 };

			Vertex2Buffer = factory.CreateBuffer(new BufferDescription(4 * VertexPositionColor.SizeInBytes, BufferUsage.VertexBuffer));
			Index2Buffer = factory.CreateBuffer(new BufferDescription(4 * sizeof(ushort), BufferUsage.IndexBuffer));

			Surface.GraphicsDevice.UpdateBuffer(Vertex2Buffer, 0, quad2Vertices);
			Surface.GraphicsDevice.UpdateBuffer(Index2Buffer, 0, quad2Indices);
			*/

			// Veldrid.SPIRV, when cross-compiling to HLSL, will always produce
			// TEXCOORD semantics; VertexElementSemantic.TextureCoordinate thus
			// becomes necessary to let D3D11 work alongside Vulkan and OpenGL.
			//
			//   https://github.com/mellinoe/veldrid/issues/121
			//
			var vertexLayout = new VertexLayoutDescription(
				new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
				new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));

			// Veldrid.SPIRV is an additional library that complements Veldrid
			// by simplifying the development of cross-backend shaders, and is
			// currently the recommended approach to doing so:
			//
			//   https://veldrid.dev/articles/portable-shaders.html
			//
			// If you decide against using it, you can try out Veldrid developer
			// mellinoe's other project, ShaderGen, or drive yourself crazy by
			// writing and maintaining custom shader code for each platform.
			byte[] vertexShaderSpirvBytes = LoadSpirvBytes(ShaderStages.Vertex);
			byte[] fragmentShaderSpirvBytes = LoadSpirvBytes(ShaderStages.Fragment);

			var options = new CrossCompileOptions();
			switch (Surface.GraphicsDevice.BackendType)
			{
				// InvertVertexOutputY and FixClipSpaceZ address two major
				// differences between Veldrid's various graphics APIs, as
				// discussed here:
				//
				//   https://veldrid.dev/articles/backend-differences.html
				//
				// Note that the only reason those options are useful in this
				// example project is that the vertices being drawn are stored
				// the way Vulkan stores vertex data. The options will therefore
				// properly convert from the Vulkan style to whatever's used by
				// the destination backend. If you store vertices in a different
				// coordinate system, these may not do anything for you, and
				// you'll need to handle the difference in your shader code.
				case GraphicsBackend.Metal:
					options.InvertVertexOutputY = true;
					break;
				case GraphicsBackend.Direct3D11:
					options.InvertVertexOutputY = true;
					break;
				case GraphicsBackend.OpenGL:
					options.FixClipSpaceZ = true;
					options.InvertVertexOutputY = true;
					break;
				default:
					break;
			}
				
			var vertex = new ShaderDescription(ShaderStages.Vertex, vertexShaderSpirvBytes, "main", true);
			var fragment = new ShaderDescription(ShaderStages.Fragment, fragmentShaderSpirvBytes, "main", true);
			Shader[] shaders = factory.CreateFromSpirv(vertex, fragment, options);

			LinePipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
			{
				BlendState = BlendStateDescription.SingleOverrideBlend,
				DepthStencilState = new DepthStencilStateDescription(
					depthTestEnabled: true,
					depthWriteEnabled: true,
					comparisonKind: ComparisonKind.LessEqual),
				RasterizerState = new RasterizerStateDescription(
					cullMode: FaceCullMode.Back,
					fillMode: PolygonFillMode.Solid,
					frontFace: FrontFace.Clockwise,
					depthClipEnabled: true,
					scissorTestEnabled: false),
				PrimitiveTopology = PrimitiveTopology.LineList,
				ResourceLayouts = new[] { viewMatrixLayout, modelMatrixLayout },
				ShaderSet = new ShaderSetDescription(
					vertexLayouts: new VertexLayoutDescription[] { vertexLayout },
					shaders: shaders),
				Outputs = Surface.Swapchain.Framebuffer.OutputDescription
			});

			LinesPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
			{
				BlendState = BlendStateDescription.SingleOverrideBlend,
				DepthStencilState = new DepthStencilStateDescription(
					depthTestEnabled: true,
					depthWriteEnabled: true,
					comparisonKind: ComparisonKind.LessEqual),
				RasterizerState = new RasterizerStateDescription(
					cullMode: FaceCullMode.Back,
					fillMode: PolygonFillMode.Solid,
					frontFace: FrontFace.Clockwise,
					depthClipEnabled: true,
					scissorTestEnabled: false),
				PrimitiveTopology = PrimitiveTopology.LineStrip,
				ResourceLayouts = new[] { viewMatrixLayout, modelMatrixLayout },
				ShaderSet = new ShaderSetDescription(
					vertexLayouts: new VertexLayoutDescription[] { vertexLayout },
					shaders: shaders),
				Outputs = Surface.Swapchain.Framebuffer.OutputDescription
			});

			FilledPipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription
			{
				BlendState = BlendStateDescription.SingleOverrideBlend,
				DepthStencilState = new DepthStencilStateDescription(
					depthTestEnabled: true,
					depthWriteEnabled: true,
					comparisonKind: ComparisonKind.LessEqual),
				RasterizerState = new RasterizerStateDescription(
					cullMode: FaceCullMode.Back,
					fillMode: PolygonFillMode.Solid,
					frontFace: FrontFace.Clockwise,
					depthClipEnabled: true,
					scissorTestEnabled: false),
				PrimitiveTopology = PrimitiveTopology.TriangleStrip,
				ResourceLayouts = new[] { modelMatrixLayout },
				ShaderSet = new ShaderSetDescription(
					vertexLayouts: new VertexLayoutDescription[] { vertexLayout },
					shaders: shaders),
				Outputs = Surface.Swapchain.Framebuffer.OutputDescription
			});

			CommandList = factory.CreateCommandList();
		}

		private byte[] LoadSpirvBytes(ShaderStages stage)
		{
			byte[] bytes;

			string shaderDir = Path.Combine(AppContext.BaseDirectory, "shaders");
			string name = $"VertexColor-{stage.ToString().ToLower()}.450.glsl";
			string full = Path.Combine(shaderDir, name);

			// Precompiled SPIR-V bytecode can speed up program start by saving
			// the need to load text files and compile them before converting
			// the result to the final backend shader format. If they're not
			// available, though, the plain .glsl files will do just fine. Look
			// up glslangValidator to learn how to compile SPIR-V binary files.
			try
			{
				bytes = File.ReadAllBytes($"{full}.spv");
			}
			catch (FileNotFoundException)
			{
				bytes = File.ReadAllBytes(full);
			}

			return bytes;
		}
	}
}
