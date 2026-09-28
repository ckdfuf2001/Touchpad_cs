# Reads the PE import table (regular + delay-load) of a native binary.
# PowerShell 5.1 compatible (C# 5 only for Add-Type).
param([string]$Path = 'C:\Program Files\TouchMousePointer\TouchMousePointer.exe',
      [string]$Filter = '')

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class PeImp3 {
    static byte[] B;
    static int[] SVA;
    static int[] SRAW;
    static int[] SSZ;
    static string ReadZ(int off) {
        if (off < 0 || off >= B.Length) return "?";
        int e = off;
        while (e < B.Length && B[e] != 0) e++;
        return Encoding.ASCII.GetString(B, off, e - off);
    }
    static int R32(int off) {
        if (off < 0 || off + 4 > B.Length) return 0;
        return BitConverter.ToInt32(B, off);
    }
    static ushort R16(int off) { return BitConverter.ToUInt16(B, off); }
    static int Map(int rva) {
        for (int i = 0; i < SVA.Length; i++) {
            if (rva >= SVA[i] && rva < SVA[i] + SSZ[i]) return SRAW[i] + (rva - SVA[i]);
        }
        return -1;
    }
    static bool Is64;
    static long R64(int off) {
        if (off < 0 || off + 8 > B.Length) return 0;
        return BitConverter.ToInt64(B, off);
    }
    static void WalkDescs(int dirRVA, List<string> out_, string tag) {
        if (dirRVA == 0) return;
        int step = Is64 ? 8 : 4;
        long ordFlag = Is64 ? unchecked((long)0x8000000000000000) : (long)unchecked((int)0x80000000);
        for (int d = 0; d < 300; d++) {
            int base_ = Map(dirRVA + d * 20);
            if (base_ < 0) break;
            int ilt = R32(base_);
            int nameRVA = R32(base_ + 12);
            int iat = R32(base_ + 16);
            if (ilt == 0 && nameRVA == 0 && iat == 0) break;
            string dll = ReadZ(Map(nameRVA));
            int thunk = ilt != 0 ? ilt : iat;
            for (int k = 0; k < 4000; k++) {
                int e = Map(thunk + k * step);
                if (e < 0) break;
                long v = Is64 ? R64(e) : (long)R32(e);
                if (v == 0) break;
                string fn;
                if ((v & ordFlag) != 0) fn = "ord" + (v & 0xFFFF);
                else fn = ReadZ(Map((int)v) + 2);
                out_.Add(tag + dll + "!" + fn);
            }
        }
    }
    static void WalkDelay(int dirRVA, List<string> out_) {
        if (dirRVA == 0) return;
        int step = Is64 ? 8 : 4;
        long ordFlag = Is64 ? unchecked((long)0x8000000000000000) : (long)unchecked((int)0x80000000);
        for (int d = 0; d < 300; d++) {
            int base_ = Map(dirRVA + d * 32);
            if (base_ < 0) break;
            int nameRVA = R32(base_ + 4);
            int iat = R32(base_ + 12);
            int names = R32(base_ + 16);
            if (nameRVA == 0 && iat == 0 && names == 0) break;
            string dll = ReadZ(Map(nameRVA));
            int thunk = names != 0 ? names : iat;
            for (int k = 0; k < 4000; k++) {
                int e = Map(thunk + k * step);
                if (e < 0) break;
                long v = Is64 ? R64(e) : (long)R32(e);
                if (v == 0) break;
                string fn;
                if ((v & ordFlag) != 0) fn = "ord" + (v & 0xFFFF);
                else fn = ReadZ(Map((int)v) + 2);
                out_.Add("DELAY-" + dll + "!" + fn);
            }
        }
    }
    public static string[] List(string path) {
        List<string> out_ = new List<string>();
        B = File.ReadAllBytes(path);
        int pe = R32(0x3C);
        int nsec = R16(pe + 6);
        int optSize = R16(pe + 20);
        int opt = pe + 24;
        bool plus = R16(opt) == 0x20B;
        Is64 = plus;
        int ddOff = opt + (plus ? 112 : 96);
        int impRVA = R32(ddOff + 8);
        int delayRVA = R32(ddOff + 13 * 8);
        SVA = new int[nsec]; SRAW = new int[nsec]; SSZ = new int[nsec];
        int secOff = pe + 24 + optSize;
        for (int i = 0; i < nsec; i++) {
            SVA[i] = R32(secOff + i * 40 + 12);
            SSZ[i] = Math.Max(R32(secOff + i * 40 + 8), R32(secOff + i * 40 + 16));
            SRAW[i] = R32(secOff + i * 40 + 20);
        }
        WalkDescs(impRVA, out_, "");
        WalkDelay(delayRVA, out_);
        return out_.ToArray();
    }
}
'@

$all = [PeImp3]::List($Path)
Write-Output ("TOTAL " + $all.Count)
if ($Filter -ne '') { $all | Where-Object { $_ -like $Filter } | Sort-Object }
else { $all | ForEach-Object { ($_ -split '!')[0] } | Sort-Object -Unique }
