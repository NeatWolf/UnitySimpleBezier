﻿using UnityEngine;
using System.Collections;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Runtime code for BezierCurve
namespace Sigtrap.Bezier {
	public partial class BezierSpline : MonoBehaviour {
		#if UNITY_EDITOR
		[MenuItem("GameObject/Create Simple Bezier")]
		public static void AddPrefab(){
			GameObject go = Instantiate(Resources.Load<GameObject>("SimpleBezierPrefab"));
			if (go){
				go.name = "SimpleBezierPrefab";
				Selection.activeGameObject = go;
			}
		}
		#endif

		public enum HandleType {CUBE, SPHERE}

		#region Curve settings
		[Header("Curve Settings")]
		[SerializeField]
		private float _strengthScale = 1;
		public float strengthScale {get {return _strengthScale;}}

		[SerializeField]
		[Tooltip("Higher values increase accuracy of constant speed mode. Shape is always accurate.")]
		private int _integrationSegments = 100;

		[SerializeField]
		private bool _closed = false;
		private bool _wasClosed = false;
		public bool closed {get {return _closed;}}
		#endregion

		#region Cached curve data
		public float length {get; private set;}
		private BezierNode[] _nodes;
		private Sector[] _sectors;

		private bool _dirty = true;
		/// <summary>
		/// When true, curve data gets recached. Called by child nodes when changed.
		/// </summary>
		public bool dirty {
			get {
				if (_closed != _wasClosed){
					_wasClosed = _closed;
					return true;
				}
				return _dirty;
			}
			set {_dirty = value;}
		}
		#endregion

		#region Public methods
		/// <summary>
		/// Calculate position at t along a manually defined Bezier curve
		/// </summary>
		/// <param name="t">T.</param>
		/// <param name="start">Start.</param>
		/// <param name="handle1">Handle1.</param>
		/// <param name="handle2">Handle2.</param>
		/// <param name="end">End.</param>
		public static Vector3 Curve(float t, Vector3 start, Vector3 handle1, Vector3 handle2, Vector3 end){
			t = Mathf.Clamp01(t);
			float u = 1f - t;
			Vector3 result = start * u*u*u;
			result += (3 * u*u * t * handle1);
			result += (3 * u * t*t * handle2);
			result += (t*t*t * end);
			return result;
		}
		/// <summary>
		/// Calculate position at t along this spline.
		/// Stateless piecewise stretch correction.
		/// </summary>
		/// <param name="t">Fractional position along curve.</param>
		/// <param name="constantSpeed">Use piecewise stretch correction?</param>
		public Vector3 Spline(float t, bool constantSpeed){
			// Ensure everything's initialised and check for cheap result at t==0||t==1
			Vector3? temp = PreBezier(ref t);
			if (temp.HasValue){
				return temp.Value;
			}

			// Use stateless piecewise stretch correction
			return GetSector(t).Curve(t, constantSpeed);
		}
		/// <summary>
		/// Calculate position at t along this spline.
		/// Stateful differential stretch correction.
		/// </summary>
		/// <param name="t">Fractional position along curve.</param>
		/// <param name="dT">Amount to increment t. If zero, uses piecewise stretch correction.</param>
		public Vector3 Spline(ref float t, float dT){
			// Ensure everything's initialised and check for cheap result at t==0||t==1
			Vector3? temp = PreBezier(ref t);
			if (temp.HasValue){
				return temp.Value;
			}

			// Use stateful differential stretch correction
			return GetSector(t).Curve(ref t, dT);
		}
		/// <summary>
		/// Get tangent to spline at point t.
		/// </summary>
		/// <param name="t">T.</param>
		/// <param name="unstretch">If set to <c>true</c> use stretch correction.</param>
		public Vector3 Tangent(float t, bool unstretch){
			return GetSector(t).TanGlobal(t, unstretch);
		}
		/// <summary>
		/// Slerp between rotations of node transforms
		/// </summary>
		/// <param name="t">T.</param>
		public Quaternion Rotation(float t){
			PreBezier(ref t);
			return GetSector(t).Rotation(t);
		}
		#endregion

		private Vector3? PreBezier(ref float t){
			if (dirty){
				GetNodes();
			}

			t = Mathf.Clamp01(t);
			if (t==0){
				return _nodes[0].transform.position;
			}
			if (t==1){
				return _nodes[_nodes.Length-1].transform.position;
			}
			
			if (dirty){
				Precache();
				dirty = false;
			}
			return null;
		}
		private bool GetNodes(){
			_nodes = GetComponentsInChildren<BezierNode>();
			if (_nodes == null || _nodes.Length == 0){
				return false;
			}
			if (_closed && _nodes.Length > 1){
				System.Array.Resize(ref _nodes, _nodes.Length + 1);
				_nodes[_nodes.Length - 1] = _nodes[0];
			}
			return true;
		}
		private void Precache(){
			// Setup sectors, calculate constant stuff etc
			_sectors = new Sector[_nodes.Length-1];
			length = 0;
			float offset = 0;
			// Loop over node pairs
			for (int i=0; i<_nodes.Length-1; ++i){
				offset = length;
				// Setup sector. Constructor does piecewise length integration.
				_sectors[i] = new Sector(_nodes[i], _nodes[i+1], offset, _integrationSegments);
				length += _sectors[i].length;
			}
			foreach (Sector s in _sectors){
				// Set global pathlength for each sector for local transformations
				s.pathLength = length;
			}
		}
		private void CacheIfDirty(){
			if (dirty){
				GetNodes();
				Precache();
				dirty = false;
			}
		}
		private Sector GetSector(float t){
			CacheIfDirty();
			if (t == 0){
				return _sectors[0];
			}
			if (t == 1){
				return _sectors[_sectors.Length-1];
			}
			for (int i=0; i<_sectors.Length; ++i){
				if (_sectors[i].InSector(t)){
					return _sectors[i];
				}
			}
			return null;
		}

		private class Sector {
			private BezierNode _start;
			private BezierNode _end;

			private float _offset;
			public float length {get; private set;}
			private float _tOffset;
			private float _tLength;
			private float[] _segmentT1s;
			public float pathLength {
				set {
					_tOffset = _offset/value;
					_tLength = length/value;
				}
			}

			private Vector3 _c1;
			private Vector3 _c2;
			private Vector3 _c3;
			private Vector3 _c4;

			public Sector(BezierNode start, BezierNode end, float offset, int integrationSegments){
				_start = start;
				_end = end;
				_offset = offset;
				_segmentT1s = new float[integrationSegments];

				Vector3 a = _start.transform.position;
				Vector3 b = _start.h2;
				Vector3 c = _end.h1;
				Vector3 d = _end.transform.position;

				// Get constant coefficients
				_c1 = (d - (3 * c) + (3 * b) - a);
				_c2 = ((3 * c) - (6 * b) + (3 * a));
				_c3 = ((3 * b) - (3 * a));
				_c4 = a;

				// Calculate segment lengths
				Vector3 s = _start.transform.position;
				Vector3 e = _end.transform.position;
				Vector3 h2 = _start.h2;
				Vector3 h1 = _end.h1;
				Vector3 p0 = _start.transform.position;
				Vector3 p1 = _start.transform.position;
				float t = 0;
				
				// Loop over integration segments along sector
				for (int i=0; i<_segmentT1s.Length; ++i){
					p1 = BezierSpline.Curve(t, s, h2, h1, e);
					// Store local length
					_segmentT1s[i] = Vector3.Distance(p1,p0);
					length += _segmentT1s[i];
					// Move to next segment
					p0 = p1;
					t += 1f/(float)_segmentT1s.Length;
				}
				
				// Get each local length as fraction, for approximate t remapping
				for (int i=0; i<_segmentT1s.Length; ++i){
					_segmentT1s[i] /= length;
				}
			}
			private float GlobalToLocalT(float t){
				if (_tOffset < 0 || _tLength <= 0){
					throw new System.Exception("Sector.pathLength must be set after calling Setup!");
				}
				return (t - _tOffset) / _tLength;
			}
			private float RemapPiecewise(float tSector){
				float t0 = 0;
				float t1 = 0;
				// Find segment t resides in, and remap according to that
				for (int segment=0; segment<_segmentT1s.Length; ++segment){
					t1 += _segmentT1s[segment];
					if (t1 > tSector){
						// Remove offset to get remainder
						tSector -= t0;
						// Get scale of segment length relative to average
						// Equivalent to seglength / (1/intSegs)
						float segScale = (t1 - t0) * (float)_segmentT1s.Length;
						// Rescale remainder
						tSector /= segScale;
						// Add linear offset back on
						tSector += ((float)segment/(float)_segmentT1s.Length);
						break;
					}
					t0 = t1;
				}
				return tSector;
			}

			/// <summary>
			/// Is the given global t within this sector?
			/// </summary>
			/// <returns><c>true</c> if given global t falls within this sector</returns>
			/// <param name="tSpline">Global t</param>
			public bool InSector(float tSpline){
				return (tSpline >= _tOffset && tSpline < (_tOffset + _tLength));
			}
			/// <summary>
			/// Calculate position from given global t, using piecewise stretch correction (or none)
			/// </summary>
			/// <param name="tSpline">Global/spline t</param>
			/// <param name="unstretch">If true, correct stretch with piecewise approximation</param>
			public Vector3 Curve(float tSpline, bool unstretch){
				tSpline = GlobalToLocalT(tSpline);
				if (unstretch){
					// Remap t
					tSpline = RemapPiecewise(tSpline);
				}
				return Curve(tSpline);
			}
			/// <summary>
			/// Calculate position from current global t and global dT using differential stretch correction
			/// If no dT given, remaps t using piecewise approximation
			/// </summary>
			/// <param name="tSpline">Global/spline t</param>
			/// <param name="dtGlobal">Global/spline delta t</param>
			public Vector3 Curve(ref float tSpline, float dtGlobal){
				tSpline = Mathf.Clamp01(tSpline);
				float tSector = GlobalToLocalT(tSpline);

				// Remap local t
				if (dtGlobal == 0){
					// If no dT, get piecewise-remapped approx of t
					tSector = RemapPiecewise(tSector);
				} else {
					// Get local derivative
					float dCdT = ((3 * _c1 * tSector * tSector) + (2 * _c2 * tSector) + _c3).magnitude;
					// Transform global dT to local, then divide by local derivative. Add transformed increment to t.
					tSector += (dtGlobal / _tLength) / dCdT;
				}

				// Transform local t back to global.
				tSpline = _tOffset + (tSector * _tLength);

				return Curve(tSector, false);
			}
			private Vector3 Curve(float tSector){
				return _c1*tSector*tSector*tSector + _c2*tSector*tSector + _c3*tSector + _c4;
			}
			public Vector3 TanLocal(float tSector, bool unstretch){
				if (unstretch){
					tSector = RemapPiecewise(tSector);
				}
				return ((3 * _c1 * tSector * tSector) + (2 * _c2 * tSector) + _c3);
			}
			public Vector3 TanGlobal(float tSpline, bool unstretch){
				tSpline = Mathf.Clamp01(tSpline);
				return TanLocal(GlobalToLocalT(tSpline), unstretch);
			}
			/// <summary>
			/// Slerp between rotations of node transforms
			/// </summary>
			/// <param name="tSpline">Global/spline t</param>
			public Quaternion Rotation(float tSpline){
				float tSector = GlobalToLocalT(Mathf.Clamp01(tSpline));
				return Quaternion.Slerp(_start.transform.rotation, _end.transform.rotation, tSector);
			}
		}
	}
}
