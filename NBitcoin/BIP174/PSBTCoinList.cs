﻿using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace NBitcoin
{
	public class PSBTHDKeyMatch<T> : PSBTHDKeyMatch where T: PSBTCoin
	{
		internal PSBTHDKeyMatch(T psbtCoin, KeyValuePair<PubKey, RootedKeyPath> kv)
			: base(psbtCoin, kv)
		{
			if (psbtCoin == null)
				throw new ArgumentNullException(nameof(psbtCoin));
			_Coin = psbtCoin;
		}

		private readonly T _Coin;
		public new T Coin
		{
			get
			{
				return _Coin;
			}
		}
	}

	public class PSBTHDKeyMatch
	{
		internal PSBTHDKeyMatch(PSBTCoin psbtCoin, KeyValuePair<PubKey, RootedKeyPath> kv)
		{
			if (psbtCoin == null)
				throw new ArgumentNullException(nameof(psbtCoin));
			_Coin = psbtCoin;
			_KeyPath = kv.Value;
			_PubKey = kv.Key;
		}

		private readonly PSBTCoin _Coin;
		public PSBTCoin Coin
		{
			get
			{
				return _Coin;
			}
		}


		private readonly PubKey _PubKey;
		public PubKey PubKey
		{
			get
			{
				return _PubKey;
			}
		}

		private readonly RootedKeyPath _KeyPath;
		public RootedKeyPath RootedKeyPath
		{
			get
			{
				return _KeyPath;
			}
		}
	}

	public class PSBTCoinList<T> : IReadOnlyList<T> where T : PSBTCoin
	{
		/// <summary>
		/// Filter the coins which contains a HD Key path matching this masterFingerprint/account key
		/// </summary>
		/// <param name="accountKey">The account key that will be used to sign (ie. 49'/0'/0')</param>
		/// <param name="accountKeyPath">The account key path</param>
		/// <returns>Inputs with HD keys matching masterFingerprint and account key</returns>
		public IEnumerable<T> CoinsFor(IHDKey accountKey, RootedKeyPath accountKeyPath = null)
		{
			return GetPSBTCoins(accountKey, accountKeyPath);
		}

		/// <summary>
		/// Filter the hd keys which contains a HD Key path matching this masterFingerprint/account key
		/// </summary>
		/// <param name="accountKey">The account key that will be used to sign (ie. 49'/0'/0')</param>
		/// <param name="accountKeyPath">The account key path</param>
		/// <returns>HD Keys matching master root key</returns>
		public IEnumerable<PSBTHDKeyMatch<T>> HDKeysFor(IHDKey accountKey, RootedKeyPath accountKeyPath = null)
		{
			return GetHDKeys(accountKey, accountKeyPath);
		}

		internal IEnumerable<T> GetPSBTCoins(IHDKey accountKey, RootedKeyPath accountKeyPath = null)
		{
			return GetHDKeys(accountKey, accountKeyPath)
							.Select(c => c.Coin)
							.Distinct();
		}
		internal IEnumerable<PSBTHDKeyMatch<T>> GetHDKeys(IHDKey accountKey, RootedKeyPath accountKeyPath = null)
		{
			if (accountKey == null)
				throw new ArgumentNullException(nameof(accountKey));
			accountKey = accountKey.AsHDKeyCache();
			var accountFingerprint = accountKey.GetPublicKey().GetHDFingerPrint();
			foreach (var c in this)
			{
				foreach (var match in c.HDKeysFor(accountKey, accountKeyPath, accountFingerprint))
				{
					yield return (PSBTHDKeyMatch<T>)match;
				}
			}
		}
		protected List<T> _Inner = new List<T>();

		public int Count => _Inner.Count;

		public T this[int index] => _Inner[index];

		public IEnumerator<T> GetEnumerator()
		{
			return _Inner.GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return _Inner.GetEnumerator();
		}
	}
}
