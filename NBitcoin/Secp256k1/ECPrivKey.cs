﻿#if HAS_SPAN
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace NBitcoin.Secp256k1
{
	/** A pointer to a function to deterministically generate a nonce.
	*
	* Returns: 1 if a nonce was successfully generated. 0 will cause signing to fail.
	* Out:     nonce32:   pointer to a 32-byte array to be filled by the function.
	* In:      msg32:     the 32-byte message hash being verified (will not be NULL)
	*          key32:     pointer to a 32-byte secret key (will not be NULL)
	*          algo16:    pointer to a 16-byte array describing the signature
	*                     algorithm (will be NULL for ECDSA for compatibility).
	*          data:      Arbitrary data pointer that is passed through.
	*          attempt:   how many iterations we have tried to find a nonce.
	*                     This will almost always be 0, but different attempt values
	*                     are required to result in a different nonce.
	*
	* Except for test cases, this function should compute some cryptographic hash of
	* the message, the algorithm, the key and the attempt.
	*/
	interface INonceFunction
	{
		bool TryGetNonce(Span<byte> nonce32, ReadOnlySpan<byte> msg32, ReadOnlySpan<byte> key32, ReadOnlySpan<byte> algo16, uint counter);
	}
	class PrecomputedNonceFunction : INonceFunction
	{
		private readonly byte[] nonce;
		public PrecomputedNonceFunction(byte[] nonce)
		{
			this.nonce = nonce;
		}
		public bool TryGetNonce(Span<byte> nonce32, ReadOnlySpan<byte> msg32, ReadOnlySpan<byte> key32, ReadOnlySpan<byte> algo16, uint counter)
		{
			nonce.AsSpan().Slice(0, 32).CopyTo(nonce32);
			return counter == 0;
		}
	}
	class RFC6979NonceFunction : INonceFunction
	{
		byte[]? data = null;
		public RFC6979NonceFunction(byte[]? nonceData = null)
		{
			this.data = nonceData;
		}
		public static RFC6979NonceFunction Instance { get; } = new RFC6979NonceFunction();
		public bool TryGetNonce(Span<byte> nonce32, ReadOnlySpan<byte> msg32, ReadOnlySpan<byte> key32, ReadOnlySpan<byte> algo16, uint counter)
		{
			Span<byte> keydata = stackalloc byte[112];
			Span<byte> originalKeyData = keydata;
			int offset = 0;
			using RFC6979HMACSHA256 rng = new RFC6979HMACSHA256();
			uint i;
			/* We feed a byte array to the PRNG as input, consisting of:
			 * - the private key (32 bytes) and message (32 bytes), see RFC 6979 3.2d.
			 * - optionally 32 extra bytes of data, see RFC 6979 3.6 Additional Data.
			 * - optionally 16 extra bytes with the algorithm name.
			 * Because the arguments have distinct fixed lengths it is not possible for
			 *  different argument mixtures to emulate each other and result in the same
			 *  nonces.
			 */

			key32.CopyTo(keydata);
			keydata = keydata.Slice(32);
			offset += 32;
			msg32.CopyTo(keydata);
			keydata = keydata.Slice(32);
			offset += 32;
			if (data != null)
			{
				data.CopyTo(keydata);
				keydata = keydata.Slice(32);
				offset += 32;
			}
			if (algo16.Length == 16)
			{
				algo16.CopyTo(keydata);
				keydata = keydata.Slice(16);
				offset += 16;
			}
			rng.Initialize(originalKeyData.Slice(0, offset));
			originalKeyData.Fill(0);
			for (i = 0; i <= counter; i++)
			{
				rng.Generate(nonce32);
			}
			return true;
		}
	}
	partial class ECPrivKey : IDisposable
	{
		bool cleared = false;
		Scalar sec;
		readonly Context ctx;

		public static bool TryCreateFromDer(ReadOnlySpan<byte> privkey, Context ctx, out ECPrivKey? result)
		{
			result = null;
			Span<byte> out32 = stackalloc byte[32];
			int lenb = 0;
			int len = 0;
			out32.Fill(0);
			/* sequence header */
			if (privkey.Length < 1 || privkey[0] != 0x30)
			{
				return false;
			}
			privkey = privkey.Slice(1);
			/* sequence length constructor */
			if (privkey.Length < 1 || (privkey[0] & 0x80) == 0)
			{
				return false;
			}
			lenb = privkey[0] & ~0x80;
			privkey = privkey.Slice(1);
			if (lenb < 1 || lenb > 2)
			{
				return false;
			}
			if (privkey.Length < lenb)
			{
				return false;
			}
			/* sequence length */
			len = privkey[lenb - 1] | (lenb > 1 ? privkey[lenb - 2] << 8 : 0);
			privkey = privkey.Slice(lenb);
			if (privkey.Length < len)
			{
				return false;
			}
			/* sequence element 0: version number (=1) */
			if (privkey.Length < 3 || privkey[0] != 0x02 || privkey[1] != 0x01 || privkey[2] != 0x01)
			{
				return false;
			}
			privkey = privkey.Slice(3);
			/* sequence element 1: octet string, up to 32 bytes */
			if (privkey.Length < 2 || privkey[0] != 0x04 || privkey[1] > 0x20 || privkey.Length < 2 + privkey[1])
			{
				return false;
			}
			privkey.Slice(2, privkey[1]).CopyTo(out32.Slice(32 - privkey[1]));
			var s = new Scalar(out32, out int overflow);
			if (overflow == 1 || s.IsZero)
			{
				out32.Fill(0);
				result = null;
				return false;
			}
			result = new ECPrivKey(s, ctx, false);
			return true;
		}
		public ECPrivKey(in Scalar scalar, Context ctx, bool enforceCheck)
		{
			if (enforceCheck)
			{
				if (scalar.IsZero || scalar.IsOverflow)
					throw new ArgumentException(paramName: nameof(scalar), message: "Invalid privkey");
			}
			else
			{
				VERIFY_CHECK(!scalar.IsZero && !scalar.IsOverflow);
			}
			sec = scalar;
			this.ctx = ctx ?? Context.Instance;
		}
		public ECPrivKey(ReadOnlySpan<byte> b32, Context ctx)
		{
			if (b32.Length != 32)
				throw new ArgumentException(paramName: nameof(b32), message: "b32 should be of length 32");
			sec = new Scalar(b32, out int overflow);
			if (overflow != 0 || sec.IsZero)
				throw new ArgumentException(paramName: nameof(b32), message: "Invalid privkey");
			this.ctx = ctx ?? Context.Instance;
		}

		public ECPubKey CreatePubKey()
		{
			if (cleared)
				throw new ObjectDisposedException(nameof(ECPrivKey));
			GroupElementJacobian pj;
			GroupElement p;
			ctx.ECMultiplicationGeneratorContext.secp256k1_ecmult_gen(out pj, sec);
			p = pj.ToGroupElement();
			var pubKey = new ECPubKey(p, ctx);
			return pubKey;
		}

		public ECPrivKey AddTweak(ReadOnlySpan<byte> tweak)
		{
			if (cleared)
				throw new ObjectDisposedException(nameof(ECPrivKey));
			if (TryAddTweak(tweak, out var r))
				return r!;
			throw new ArgumentException(paramName: nameof(tweak), message: "Invalid tweak");
		}
		public bool TryAddTweak(ReadOnlySpan<byte> tweak, out ECPrivKey? tweakedPrivKey)
		{
			tweakedPrivKey = null;
			if (this.cleared)
				return false;
			tweakedPrivKey = null;
			if (tweak.Length < 32)
				return false;
			Scalar term;
			ECPrivKey seckey;
			bool ret;
			int overflow = 0;
			term = new Scalar(tweak, out overflow);

			Scalar sec = this.sec;
			ret = overflow == 0 && secp256k1_eckey_privkey_tweak_add(ref sec, term);
			if (ret)
			{
				seckey = new ECPrivKey(sec, ctx, false);
				tweakedPrivKey = seckey;
			}
			sec = default;
			term = default;
			return ret;
		}

		private bool secp256k1_eckey_privkey_tweak_add(ref Scalar key, in Scalar tweak)
		{
			key += tweak;
			return !key.IsZero;
		}

		public void WriteDerToSpan(bool compressed, Span<byte> derOutput, out int length)
		{
			if (cleared)
				throw new ObjectDisposedException(nameof(ECPrivKey));
			ECPubKey pubkey = CreatePubKey();
			if (compressed)
			{
				Span<byte> begin = stackalloc byte[] { 0x30, 0x81, 0xD3, 0x02, 0x01, 0x01, 0x04, 0x20 };
				Span<byte> middle = stackalloc byte[] {
					0xA0,
					0x81,
					0x85,
					0x30,
					0x81,
					0x82,
					0x02,
					0x01,
					0x01,
					0x30,
					0x2C,
					0x06,
					0x07,
					0x2A,
					0x86,
					0x48,
					0xCE,
					0x3D,
					0x01,
					0x01,
					0x02,
					0x21,
					0x00,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFE,
					0xFF,
					0xFF,
					0xFC,
					0x2F,
					0x30,
					0x06,
					0x04,
					0x01,
					0x00,
					0x04,
					0x01,
					0x07,
					0x04,
					0x21,
					0x02,
					0x79,
					0xBE,
					0x66,
					0x7E,
					0xF9,
					0xDC,
					0xBB,
					0xAC,
					0x55,
					0xA0,
					0x62,
					0x95,
					0xCE,
					0x87,
					0x0B,
					0x07,
					0x02,
					0x9B,
					0xFC,
					0xDB,
					0x2D,
					0xCE,
					0x28,
					0xD9,
					0x59,
					0xF2,
					0x81,
					0x5B,
					0x16,
					0xF8,
					0x17,
					0x98,
					0x02,
					0x21,
					0x00,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFE,
					0xBA,
					0xAE,
					0xDC,
					0xE6,
					0xAF,
					0x48,
					0xA0,
					0x3B,
					0xBF,
					0xD2,
					0x5E,
					0x8C,
					0xD0,
					0x36,
					0x41,
					0x41,
					0x02,
					0x01,
					0x01,
					0xA1,
					0x24,
					0x03,
					0x22,
					0x00
				};
				var ptr = derOutput;
				begin.CopyTo(ptr);
				ptr = ptr.Slice(begin.Length);
				sec.WriteToSpan(ptr);
				ptr = ptr.Slice(32);
				middle.CopyTo(ptr);
				ptr = ptr.Slice(middle.Length);
				pubkey.WriteToSpan(true, ptr, out var lenptr);
				length = begin.Length + 32 + middle.Length + lenptr;
			}
			else
			{
				Span<byte> begin = stackalloc byte[] { 0x30, 0x82, 0x01, 0x13, 0x02, 0x01, 0x01, 0x04, 0x20 };
				Span<byte> middle = stackalloc byte[] {
					0xA0,
					0x81,
					0xA5,
					0x30,
					0x81,
					0xA2,
					0x02,
					0x01,
					0x01,
					0x30,
					0x2C,
					0x06,
					0x07,
					0x2A,
					0x86,
					0x48,
					0xCE,
					0x3D,
					0x01,
					0x01,
					0x02,
					0x21,
					0x00,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFE,
					0xFF,
					0xFF,
					0xFC,
					0x2F,
					0x30,
					0x06,
					0x04,
					0x01,
					0x00,
					0x04,
					0x01,
					0x07,
					0x04,
					0x41,
					0x04,
					0x79,
					0xBE,
					0x66,
					0x7E,
					0xF9,
					0xDC,
					0xBB,
					0xAC,
					0x55,
					0xA0,
					0x62,
					0x95,
					0xCE,
					0x87,
					0x0B,
					0x07,
					0x02,
					0x9B,
					0xFC,
					0xDB,
					0x2D,
					0xCE,
					0x28,
					0xD9,
					0x59,
					0xF2,
					0x81,
					0x5B,
					0x16,
					0xF8,
					0x17,
					0x98,
					0x48,
					0x3A,
					0xDA,
					0x77,
					0x26,
					0xA3,
					0xC4,
					0x65,
					0x5D,
					0xA4,
					0xFB,
					0xFC,
					0x0E,
					0x11,
					0x08,
					0xA8,
					0xFD,
					0x17,
					0xB4,
					0x48,
					0xA6,
					0x85,
					0x54,
					0x19,
					0x9C,
					0x47,
					0xD0,
					0x8F,
					0xFB,
					0x10,
					0xD4,
					0xB8,
					0x02,
					0x21,
					0x00,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFF,
					0xFE,
					0xBA,
					0xAE,
					0xDC,
					0xE6,
					0xAF,
					0x48,
					0xA0,
					0x3B,
					0xBF,
					0xD2,
					0x5E,
					0x8C,
					0xD0,
					0x36,
					0x41,
					0x41,
					0x02,
					0x01,
					0x01,
					0xA1,
					0x44,
					0x03,
					0x42,
					0x00
				};
				var ptr = derOutput;
				begin.CopyTo(ptr);
				ptr = ptr.Slice(begin.Length);
				sec.WriteToSpan(ptr);
				ptr = ptr.Slice(32);
				middle.CopyTo(ptr);
				ptr = ptr.Slice(middle.Length);
				pubkey.WriteToSpan(false, ptr, out var lenptr);
				length = begin.Length + 32 + middle.Length + lenptr;
			}
		}

		public void WriteToSpan(Span<byte> span)
		{
			if (this.cleared)
				throw new ObjectDisposedException(nameof(ECPrivKey));
			this.sec.WriteToSpan(span);
		}

		public SecpECDSASignature SignECDSARFC6979(ReadOnlySpan<byte> msg32)
		{
			var ret = TrySignECDSA(msg32, out var sig);
			VERIFY_CHECK(ret); // RFC6979 can't fail
			if (sig is null)
				throw new InvalidOperationException("SignECDSARFC6979 failed (bug in C# secp256k1)");
			return sig;
		}
		[Conditional("SECP256K1_VERIFY")]
		private static void VERIFY_CHECK(bool value)
		{
			if (!value)
				throw new InvalidOperationException("VERIFY_CHECK failed (bug in C# secp256k1)");
		}
		public bool TrySignECDSA(ReadOnlySpan<byte> msg32, out SecpECDSASignature? signature)
		{
			return TrySignECDSA(msg32, null, out signature);
		}
		public bool TrySignECDSA(ReadOnlySpan<byte> msg32, INonceFunction? nonceFunction, out SecpECDSASignature? signature)
		{
			return TrySignECDSA(msg32, nonceFunction, out _, out signature);
		}
		public bool TrySignECDSA(ReadOnlySpan<byte> msg32, INonceFunction? nonceFunction, out int recid, out SecpECDSASignature? signature)
		{
			recid = 0;
			signature = null;
			if (cleared)
				return false;
			Scalar r, s;
			r = default;
			s = default;
			Scalar non, msg;
			bool ret = false;
			int overflow = 0;
			if (msg32.Length != 32)
				return false;
			if (nonceFunction == null)
			{
				nonceFunction = RFC6979NonceFunction.Instance;
			}

			/* Private key is valid, enforced by ctor */
			VERIFY_CHECK(!sec.IsZero && !sec.IsOverflow);
			Span<byte> nonce32 = stackalloc byte[32];
			Span<byte> seckey = stackalloc byte[32];
			sec.WriteToSpan(seckey);
			uint count = 0;
			msg = new Scalar(msg32, out _);
			var alg16 = new ReadOnlySpan<byte>();
			while (true)
			{
				ret = nonceFunction.TryGetNonce(nonce32, msg32, seckey, alg16, count);
				if (!ret)
				{
					break;
				}
				non = new Scalar(nonce32, out overflow);
				if (overflow == 0 && !non.IsZero)
				{
					if (secp256k1_ecdsa_sig_sign(ctx.ECMultiplicationGeneratorContext, out r, out s, sec, msg, non, out recid))
					{
						break;
					}
				}
				count++;
			}
			nonce32.Fill(0);
			msg = default;
			non = default;
			seckey.Fill(0);

			if (ret)
			{
				signature = new SecpECDSASignature(r, s, false);
			}
			else
			{
				signature = null;
			}
			return ret;
		}

		internal static bool secp256k1_ecdsa_sig_sign(ECMultiplicationGeneratorContext ctx, out Scalar sigr, out Scalar sigs, in Scalar seckey, in Scalar message, in Scalar nonce, out int recid)
		{
			Span<byte> b = stackalloc byte[32];
			GroupElementJacobian rp;
			GroupElement r;
			Scalar n;
			int overflow = 0;

			ctx.secp256k1_ecmult_gen(out rp, nonce);
			r = rp.ToGroupElement();
			r = new GroupElement(r.x.NormalizeVariable(), r.y.NormalizeVariable());
			r.x.WriteToSpan(b);
			sigr = new Scalar(b, out overflow);
			/* These two conditions should be checked before calling */
			if (sigr.IsZero || overflow != 0)
				throw new InvalidOperationException("Invalid sigr");

			/* The overflow condition is cryptographically unreachable as hitting it requires finding the discrete log
			 * of some P where P.x >= order, and only 1 in about 2^127 points meet this criteria.
			 */
			recid = (overflow != 0 ? 2 : 0) | (r.y.IsOdd ? 1 : 0);

			n = sigr * seckey;
			n += message;
			sigs = nonce.Inverse();
			sigs *= n;
			n = default;
			rp = default;
			r = default;
			if (sigs.IsZero)
			{
				return false;
			}
			if (sigs.IsHigh)
			{
				sigs = sigs.Negate();
				recid ^= 1;
			}
			return true;
		}

		public override bool Equals(object obj)
		{
			if (obj is ECPrivKey item)
			{
				return this == item;
			}
			return false;
		}
		public static bool operator ==(ECPrivKey? a, ECPrivKey? b)
		{
			if (a is ECPrivKey aa && b is ECPrivKey bb)
			{
				return aa.sec == bb.sec;
			}
			return a is null && b is null;
		}

		public static bool operator !=(ECPrivKey? a, ECPrivKey? b)
		{
			return !(a == b);
		}

		public override int GetHashCode()
		{
			return sec.GetHashCode();
		}

		public ECPrivKey MultTweak(ReadOnlySpan<byte> tweak)
		{
			if (cleared)
				throw new ObjectDisposedException(nameof(ECPrivKey));
			if (TryMultTweak(tweak, out var r))
				return r!;
			throw new ArgumentException(paramName: nameof(tweak), message: "Invalid tweak");
		}

		public bool TryMultTweak(ReadOnlySpan<byte> tweak, out ECPrivKey? tweakedPrivkey)
		{
			tweakedPrivkey = null;
			if (cleared)
				return false;
			tweakedPrivkey = null;
			if (tweak.Length < 32)
				return false;
			Scalar factor;
			bool ret = false;
			int overflow = 0;
			factor = new Scalar(tweak, out overflow);
			var sec = this.sec;
			ret = overflow == 0 && secp256k1_eckey_privkey_tweak_mul(ref sec, factor);
			if (ret)
			{
				tweakedPrivkey = new ECPrivKey(sec, ctx, false);
			}
			sec = default;
			factor = default;
			return ret;
		}

		private bool secp256k1_eckey_privkey_tweak_mul(ref Scalar key, in Scalar tweak)
		{
			if (tweak.IsZero)
				return false;
			key *= tweak;
			return true;
		}

		public void Dispose()
		{
			Clear();
		}

		public void Clear()
		{
			this.sec = default;
			this.cleared = true;
		}
		public ECPrivKey Clone()
		{
			if (this.cleared)
				throw new ObjectDisposedException(nameof(ECPrivKey));
			return new ECPrivKey(this.sec, this.ctx, false);
		}
	}
}
#nullable restore
#endif
