﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace NBitcoin.Secp256k1
{
	readonly struct GroupElement
	{
		/** Prefix byte used to tag various encoded curvepoints for specific purposes */
		public const byte SECP256K1_TAG_PUBKEY_EVEN = 0x02;
		public const byte SECP256K1_TAG_PUBKEY_ODD = 0x03;
		public const byte SECP256K1_TAG_PUBKEY_UNCOMPRESSED = 0x04;
		public const byte SECP256K1_TAG_PUBKEY_HYBRID_EVEN = 0x06;
		public const byte SECP256K1_TAG_PUBKEY_HYBRID_ODD = 0x07;

		internal readonly FieldElement x;
		internal readonly FieldElement y;
		internal readonly bool infinity; /* whether this represents the point at infinity */
		static readonly GroupElement _Infinity = new GroupElement(FieldElement.Zero, FieldElement.Zero, true);
		/** Generator for secp256k1, value 'g' defined in
 *  "Standards for Efficient Cryptography" (SEC2) 2.7.1.
 */
		public static ref readonly GroupElement Infinity => ref _Infinity;

		public static GroupElement SECP256K1_GE_CONST(uint a, uint b, uint c, uint d, uint e, uint f, uint g, uint h, uint i, uint j, uint k, uint l, uint m, uint n, uint o, uint p)
		{
			return new GroupElement(
				FieldElement.SECP256K1_FE_CONST(a, b, c, d, e, f, g, h),
				FieldElement.SECP256K1_FE_CONST(i, j, k, l, m, n, o, p),
				false
				);
		}

		public readonly bool IsInfinity
		{
			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			get
			{
				return infinity;
			}
		}

		static readonly GroupElement _Zero = new GroupElement(FieldElement.Zero, FieldElement.Zero, false);
		public static ref readonly GroupElement Zero => ref _Zero;

		public bool IsValidVariable
		{
			get
			{
				FieldElement y2, x3, c;
				if (infinity)
				{
					return false;
				}
				/* y^2 = x^3 + 7 */
				y2 = y.Sqr();
				x3 = x.Sqr();
				x3 = x3 * x;
				c = new FieldElement(EC.CURVE_B);
				x3 += c;
				x3 = x3.NormalizeWeak();
				return y2.EqualsVariable(x3);
			}
		}

		const int SIZE_MAX = int.MaxValue;
		public static void SetAllGroupElementJacobianVariable(Span<GroupElement> r, ReadOnlySpan<GroupElementJacobian> a, int len)
		{
			FieldElement u;
			int i;
			int last_i = SIZE_MAX;

			for (i = 0; i < len; i++)
			{
				if (!a[i].infinity)
				{
					/* Use destination's x coordinates as scratch space */
					if (last_i == SIZE_MAX)
					{
						r[i] = new GroupElement(a[i].z, r[i].y, r[i].infinity);
					}
					else
					{
						FieldElement rx = r[last_i].x * a[i].z;
						r[i] = new GroupElement(rx, r[i].y, r[i].infinity);
					}
					last_i = i;
				}
			}
			if (last_i == SIZE_MAX)
			{
				return;
			}
			u = r[last_i].x.InverseVariable();

			i = last_i;
			while (i > 0)
			{
				i--;
				if (!a[i].infinity)
				{
					FieldElement rx = r[i].x * u;
					r[last_i] = new GroupElement(rx, r[last_i].y, r[last_i].infinity);
					u = u * a[last_i].z;
					last_i = i;
				}
			}
			VERIFY_CHECK(!a[last_i].infinity);
			r[last_i] = new GroupElement(u, r[last_i].y, r[last_i].infinity);

			for (i = 0; i < len; i++)
			{
				r[i] = new GroupElement(r[i].x, r[i].y, a[i].infinity);
				if (!a[i].infinity)
				{
					r[i] = a[i].ToGroupElementZInv(r[i].x);
				}
			}
		}

		[Conditional("SECP256K1_VERIFY")]
		private static void VERIFY_CHECK(bool value)
		{
			if (!value)
				throw new InvalidOperationException("VERIFY_CHECK failed (bug in C# secp256k1)");
		}

		public static bool TryCreateXQuad(FieldElement x, out GroupElement result)
		{
			result = GroupElement.Zero;
			FieldElement rx, ry;
			bool rinfinity;
			FieldElement x2, x3, c;
			rx = x;
			x2 = x.Sqr();
			x3 = x * x2;
			rinfinity = false;
			c = new FieldElement(EC.CURVE_B);
			c += x3;
			if (!c.Sqrt(out ry))
				return false;
			result = new GroupElement(rx, ry, rinfinity);
			return true;
		}
		public static bool TryCreateXOVariable(FieldElement x, bool odd, out GroupElement result)
		{
			if (!TryCreateXQuad(x, out result))
				return false;
			var ry = result.y.NormalizeVariable();
			if (ry.IsOdd != odd)
			{
				ry = ry.Negate(1);
			}
			result = new GroupElement(result.x, ry, result.infinity);
			return true;
		}

		static readonly FieldElement beta = FieldElement.SECP256K1_FE_CONST(
	0x7ae96a2bu, 0x657c0710u, 0x6e64479eu, 0xac3434e9u,
	0x9cf04975u, 0x12f58995u, 0xc1396c28u, 0x719501eeu
		);
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public readonly GroupElement MultiplyLambda()
		{
			return new GroupElement(x * beta, y, infinity);
		}

		public GroupElement(in FieldElement x, in FieldElement y, bool infinity)
		{
			this.x = x;
			this.y = y;
			this.infinity = infinity;
		}
		public GroupElement(in FieldElement x, in FieldElement y)
		{
			this.x = x;
			this.y = y;
			this.infinity = false;
		}

		public readonly void Deconstruct(out FieldElement x, out FieldElement y, out bool infinity)
		{
			x = this.x;
			y = this.y;
			infinity = this.infinity;
		}
		[MethodImpl(MethodImplOptions.NoOptimization)]
		public readonly GroupElement ZInv(in GroupElement a, in FieldElement zi)
		{
			var (x, y, infinity) = this;
			FieldElement zi2 = zi.Sqr();
			FieldElement zi3 = zi2 * zi;
			x = a.x * zi2;
			y = a.y * zi3;
			infinity = a.infinity;
			return new GroupElement(x, y, infinity);
		}

		[MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.AggressiveInlining)]
		public readonly GroupElement NormalizeY()
		{
			return new GroupElement(x, this.y.Normalize(), infinity);
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public readonly GroupElement NormalizeYVariable()
		{
			return new GroupElement(x, this.y.NormalizeVariable(), infinity);
		}

		[MethodImpl(MethodImplOptions.NoOptimization)]
		public readonly GroupElement Negate()
		{
			var ry = y.NormalizeWeak();
			ry = ry.Negate(1);
			return new GroupElement(x, ry, infinity);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public readonly GroupElementJacobian ToGroupElementJacobian()
		{
			return new GroupElementJacobian(x, y, new FieldElement(1), infinity);
		}

		public readonly string ToC(string varName)
		{
			StringBuilder b = new StringBuilder();
			b.AppendLine(x.ToC($"{varName}x"));
			b.AppendLine(y.ToC($"{varName}y"));
			var infinitystr = infinity ? 1 : 0;
			b.AppendLine($"int {varName}infinity = {infinitystr};");
			b.AppendLine($"secp256k1_ge {varName} = {{ {varName}x, {varName}y, {varName}infinity }};");
			return b.ToString();
		}

		public readonly GroupElementStorage ToStorage()
		{
			VERIFY_CHECK(!infinity);
			return new GroupElementStorage(x, y);
		}

		[MethodImpl(MethodImplOptions.NoOptimization)]
		public readonly GroupElementJacobian ECMultiplyConst(in Scalar scalar, int size)
		{
			Span<GroupElement> pre_a = stackalloc GroupElement[ECMultiplicationContext.ArraySize_A];
			GroupElement tmpa;
			FieldElement Z = default;

			int skew_1;

			Span<GroupElement> pre_a_lam = stackalloc GroupElement[ECMultiplicationContext.ArraySize_A];
			Span<int> wnaf_lam = stackalloc int[1 + ECMultiplicationContext.WNAFT_SIZE_A];
			int skew_lam;
			Scalar q_1, q_lam;

			Span<int> wnaf_1 = stackalloc int[1 + ECMultiplicationContext.WNAFT_SIZE_A];

			int i;
			Scalar sc = scalar;

			/* build wnaf representation for q. */
			int rsize = size;
			if (size > 128)
			{
				rsize = 128;
				/* split q into q_1 and q_lam (where q = q_1 + q_lam*lambda, and q_1 and q_lam are ~128 bit) */
				sc.SplitLambda(out q_1, out q_lam);
				skew_1 = Wnaf.Const(wnaf_1, q_1, ECMultiplicationContext.WINDOW_A - 1, 128);
				skew_lam = Wnaf.Const(wnaf_lam, q_lam, ECMultiplicationContext.WINDOW_A - 1, 128);
			}
			else
			{
				skew_1 = Wnaf.Const(wnaf_1, sc, ECMultiplicationContext.WINDOW_A - 1, size);
				skew_lam = 0;
			}

			/* Calculate odd multiples of a.
     * All multiples are brought to the same Z 'denominator', which is stored
     * in Z. Due to secp256k1' isomorphism we can do all operations pretending
     * that the Z coordinate was 1, use affine addition formulae, and correct
     * the Z coordinate of the result once at the end.
     */
			GroupElementJacobian r = this.ToGroupElementJacobian();
			ECMultiplicationContext.secp256k1_ecmult_odd_multiples_table_globalz_windowa(pre_a, ref Z, r);
			for (i = 0; i < ECMultiplicationContext.ArraySize_A; i++)
			{
				pre_a[i] = new GroupElement(pre_a[i].x, pre_a[i].y.NormalizeWeak(), pre_a[i].infinity);
			}
			if (size > 128)
			{
				for (i = 0; i < ECMultiplicationContext.ArraySize_A; i++)
				{
					pre_a_lam[i] = pre_a[i].MultiplyLambda();
				}
			}

			/* first loop iteration (separated out so we can directly set r, rather
			 * than having it start at infinity, get doubled several times, then have
			 * its new value added to it) */
			i = wnaf_1[Wnaf.SIZE_BITS(rsize, ECMultiplicationContext.WINDOW_A - 1)];
			VERIFY_CHECK(i != 0);
			tmpa = ECMULT_CONST_TABLE_GET_GE(pre_a, i, ECMultiplicationContext.WINDOW_A);
			r = tmpa.ToGroupElementJacobian();
			if (size > 128)
			{
				i = wnaf_lam[Wnaf.SIZE_BITS(rsize, ECMultiplicationContext.WINDOW_A - 1)];
				VERIFY_CHECK(i != 0);
				tmpa = ECMULT_CONST_TABLE_GET_GE(pre_a_lam, i, ECMultiplicationContext.WINDOW_A);
				r = r + tmpa;
			}
			/* remaining loop iterations */
			for (i = Wnaf.SIZE_BITS(rsize, ECMultiplicationContext.WINDOW_A - 1) - 1; i >= 0; i--)
			{
				int n;
				int j;
				for (j = 0; j < ECMultiplicationContext.WINDOW_A - 1; ++j)
				{
					r = r.DoubleNonZero();
				}

				n = wnaf_1[i];
				tmpa = ECMULT_CONST_TABLE_GET_GE(pre_a, n, ECMultiplicationContext.WINDOW_A);
				VERIFY_CHECK(n != 0);
				r = r + tmpa;
				if (size > 128)
				{
					n = wnaf_lam[i];
					tmpa = tmpa.ECMULT_CONST_TABLE_GET_GE(pre_a_lam, n, ECMultiplicationContext.WINDOW_A);
					VERIFY_CHECK(n != 0);
					r = r + tmpa;
				}
			}

			r = new GroupElementJacobian(r.x, r.y, r.z * Z, r.infinity);

			{
				/* Correct for wNAF skew */
				GroupElement correction = this;
				GroupElementStorage correction_1_stor;
				GroupElementStorage correction_lam_stor = default;
				GroupElementStorage a2_stor;
				GroupElementJacobian tmpj = correction.ToGroupElementJacobian();
				tmpj = tmpj.DoubleVariable();
				correction = tmpj.ToGroupElement();
				correction_1_stor = this.ToStorage();
				if (size > 128)
				{
					correction_lam_stor = this.ToStorage();
				}
				a2_stor = correction.ToStorage();

				/* For odd numbers this is 2a (so replace it), for even ones a (so no-op) */
				GroupElementStorage.CMov(ref correction_1_stor, a2_stor, skew_1 == 2 ? 1 : 0);
				if (size > 128)
				{
					GroupElementStorage.CMov(ref correction_lam_stor, a2_stor, skew_lam == 2 ? 1 : 0);
				}

				/* Apply the correction */
				correction = correction_1_stor.ToGroupElement();
				correction = correction.Negate();
				r = r + correction;

				if (size > 128)
				{
					correction = correction_lam_stor.ToGroupElement();
					correction = correction.Negate();
					correction = correction.MultiplyLambda();
					r = r + correction;
				}
			}
			return r;
		}

		/* This is like `ECMULT_TABLE_GET_GE` but is constant time */
		private GroupElement ECMULT_CONST_TABLE_GET_GE(Span<GroupElement> pre, int n, int w)
		{
			int m;
			int abs_n = (n) * (((n) > 0 ? 1 : 0) * 2 - 1);
			int idx_n = abs_n / 2;
			FieldElement neg_y;
			VERIFY_CHECK(((n) & 1) == 1);
			VERIFY_CHECK((n) >= -((1 << ((w) - 1)) - 1));
			VERIFY_CHECK((n) <= ((1 << ((w) - 1)) - 1));
			var rx = FieldElement.Zero;
			var ry = FieldElement.Zero;

			for (m = 0; m < ECMULT_TABLE_SIZE(w); m++)
			{
				/* This loop is used to avoid secret data in array indices. See
				 * the comment in ecmult_gen_impl.h for rationale. */
				FieldElement.CMov(ref rx, (pre)[m].x, m == idx_n ? 1 : 0);
				FieldElement.CMov(ref ry, (pre)[m].y, m == idx_n ? 1 : 0);
			}
			var rinfinity = false;
			neg_y = ry.Negate(1);
			FieldElement.CMov(ref ry, neg_y, (n) != abs_n ? 1 : 0);
			return new GroupElement(rx, ry, rinfinity);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static int ECMULT_TABLE_SIZE(int w)
		{
			return 1 << ((w) - 2);
		}
	}
}
