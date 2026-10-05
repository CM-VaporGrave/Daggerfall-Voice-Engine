using System.Buffers.Binary;

namespace DaggerfallVoiceEngine;

public static class AudioDsp
{
    public const int KokoroRate = 24000;

    public static float[] Pcm16ToFloat(byte[] pcm)
    {
        var result = new float[pcm.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768f;
        return result;
    }

    public static byte[] FloatToWave(float[] audio, int sampleRate, bool eightBitUnsigned = false)
    {
        audio = NormalizePeak(audio);
        int bits = eightBitUnsigned ? 8 : 16;
        int dataSize = audio.Length * (bits / 8);
        byte[] wav = new byte[44 + dataSize];
        using var ms = new MemoryStream(wav);
        using var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sampleRate); bw.Write(sampleRate * (bits / 8)); bw.Write((short)(bits / 8)); bw.Write((short)bits);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data")); bw.Write(dataSize);
        if (eightBitUnsigned)
            foreach (float f in audio) bw.Write((byte)Math.Clamp((int)Math.Round((Math.Clamp(f, -1f, 1f) + 1f) * 127.5f), 0, 255));
        else
            foreach (float f in audio) bw.Write((short)Math.Clamp((int)Math.Round(Math.Clamp(f, -1f, 1f) * 32767f), short.MinValue, short.MaxValue));
        return wav;
    }

    public static float[] Apply(SynthesisRequest r, float[] input, out int sampleRate, out bool u8)
    {
        float[] y = PitchShift(input, r.PitchSemitones);
        y = CharacterDsp(y, r);
        string style = (r.AudioStyle ?? "clean").Trim().ToLowerInvariant();
        u8 = false; sampleRate = KokoroRate;
        // Keep the named sample-rate/bit-depth targets authentic, but make the legacy profiles
        // audibly distinct after Unity resamples them to the output device. The extra low-pass is
        // intentionally modest for CD-ROM and stronger for DOS.
        if (style == "cdrom")
        {
            y = MovingAverage(y, 3);
            y = Resample(y, KokoroRate, 22050);
            sampleRate = 22050;
        }
        else if (style == "dos")
        {
            y = MovingAverage(y, 6);
            y = Resample(y, KokoroRate, 11025);
            y = MovingAverage(y, 2);
            sampleRate = 11025;
            u8 = true;
        }
        return NormalizePeak(y);
    }

    public static void ApplyEmotion(SynthesisRequest r)
    {
        float i = Math.Clamp(r.EmotionIntensity, 0f, 1f);
        if (i <= 0.0001f) return;
        switch ((r.Emotion ?? "neutral").Trim().ToLowerInvariant())
        {
            case "warm": r.Speed *= Lerp(1f, .97f, i); r.PitchSemitones += .10f * i; r.Presence += .05f * i; r.Breath += .04f * i; break;
            case "amused": r.Speed *= Lerp(1f, 1.04f, i); r.PitchSemitones += .22f * i; r.Presence += .07f * i; break;
            case "suspicious": r.Speed *= Lerp(1f, .93f, i); r.PitchSemitones -= .12f * i; r.Compression += .06f * i; break;
            case "tense": r.Speed *= Lerp(1f, 1.04f, i); r.PitchSemitones += .16f * i; r.Compression += .10f * i; r.Presence += .06f * i; break;
            case "angry": r.Speed *= Lerp(1f, 1.07f, i); r.PitchSemitones -= .08f * i; r.Saturation += .12f * i; r.Presence += .10f * i; r.Compression += .15f * i; break;
            case "afraid": r.Speed *= Lerp(1f, 1.10f, i); r.PitchSemitones += .32f * i; r.FlutterDepth += .08f * i; r.FlutterRate = Math.Max(r.FlutterRate, 6f * i); break;
            case "grim": r.Speed *= Lerp(1f, .90f, i); r.PitchSemitones -= .24f * i; r.Compression += .08f * i; break;
            case "triumphant": r.Speed *= Lerp(1f, .98f, i); r.PitchSemitones += .14f * i; r.Presence += .12f * i; r.Compression += .06f * i; break;
            case "weary": r.Speed *= Lerp(1f, .87f, i); r.PitchSemitones -= .16f * i; r.Breath += .10f * i; break;
        }
        r.Speed = Math.Clamp(r.Speed, .5f, 2f);
        r.PitchSemitones = Math.Clamp(r.PitchSemitones, -6f, 4f);
    }

    static float[] CharacterDsp(float[] x, SynthesisRequest r)
    {
        float gravel = Clamp01(r.Gravel), saturation = Clamp01(r.Saturation), presence = Clamp01(r.Presence), compression = Clamp01(r.Compression);
        float doubleMix = Math.Clamp(r.DoubleMix, 0f, .6f), spectral = Clamp01(r.Spectral), reverb = Clamp01(r.Reverb);
        float subMix = Math.Clamp(r.SubharmonicMix, 0f, .65f), hiss = Clamp01(r.Hiss), throat = Clamp01(r.ThroatResonance);
        float flutterDepth = Math.Clamp(r.FlutterDepth, 0f, .5f), flutterRate = Math.Clamp(r.FlutterRate, 0f, 12f), croak = Clamp01(r.Croak);
        float purrMix = Math.Clamp(r.PurrMix, 0f, .5f), purrRate = Math.Clamp(r.PurrRate, 0f, 70f), feline = Clamp01(r.FelineResonance);
        float growl = Clamp01(r.Growl), breath = Clamp01(r.Breath);
        float[] y = (float[])x.Clone();

        if (compression > .0001f) { float drive = 1f + compression * 5f, den = 1f - MathF.Exp(-drive); for (int n=0;n<y.Length;n++) y[n] = MathF.Sign(y[n]) * (1f - MathF.Exp(-MathF.Abs(y[n]) * drive)) / den; }
        if (saturation > .0001f) { float drive = 1f + saturation * 5f, den = MathF.Tanh(drive); for (int n=0;n<y.Length;n++) { float sat = MathF.Tanh(y[n]*drive) / Math.Max(1e-6f,den); y[n]=y[n]*(1f-saturation*.55f)+sat*(saturation*.55f); } }
        if (presence > .0001f && y.Length > 2) { var o=(float[])y.Clone(); for(int n=1;n<y.Length;n++) y[n]+= (o[n]-.92f*o[n-1])*(presence*.30f); }
        if (gravel > .0001f && y.Length > 8) { var smooth=MovingAverage(y,7); for(int n=0;n<y.Length;n++){ float hp=y[n]-smooth[n]; y[n]+=MathF.Tanh(hp*(4f+gravel*7f))*(.05f+gravel*.16f);} }
        if (doubleMix > .0001f) { var doubled=PitchShift(y,Math.Clamp(r.DoublePitchSemitones,-4f,4f)); int delay=(int)Math.Round(KokoroRate*Math.Clamp(r.DoubleDelayMs,0f,80f)/1000f); var layer=new float[y.Length]; int count=Math.Min(doubled.Length,Math.Max(0,y.Length-delay)); if(count>0) Array.Copy(doubled,0,layer,delay,count); for(int n=0;n<y.Length;n++) y[n]=y[n]*(1f-doubleMix*.35f)+layer[n]*doubleMix; }
        if (subMix > .0001f) { var sub=Fit(PitchShift(y,Math.Clamp(r.SubharmonicPitch,-12f,-1f)),y.Length); sub=MovingAverage(sub,9); var env=NormalizeEnvelope(MovingAverage(Abs(y),Math.Max(9,(int)(KokoroRate*.010f)))); for(int n=0;n<y.Length;n++) y[n]+=sub[n]*env[n]*(.62f*subMix); }
        if (throat > .0001f && y.Length>32) { var body=MovingAverage(y,5); var res=new float[y.Length]; DelayAdd(res,body,5.5f,.42f); DelayAdd(res,body,9.5f,-.24f); DelayAdd(res,body,14f,.15f); for(int n=0;n<y.Length;n++) y[n]+=res[n]*(.55f*throat); }
        if (croak > .0001f && y.Length>16) { var body=MovingAverage(y,11); var env=NormalizeEnvelope(MovingAverage(Abs(y),Math.Max(9,(int)(KokoroRate*.008f)))); for(int n=0;n<y.Length;n++) y[n]+=MathF.Tanh(body[n]*(4.5f+5.5f*croak))*env[n]*(.11f+.19f*croak); }
        if (hiss > .0001f && y.Length>8) { var smooth=MovingAverage(y,13); var high=new float[y.Length]; for(int n=0;n<y.Length;n++) high[n]=y[n]-smooth[n]; var gate=NormalizeEnvelope(MovingAverage(Abs(high),5)); for(int n=0;n<y.Length;n++) y[n]+=MathF.Tanh(high[n]*(3f+hiss*4f))*gate[n]*(.07f+.20f*hiss); }
        if (flutterDepth>.0001f && flutterRate>.0001f) { var f=Flutter(y,flutterDepth,flutterRate); for(int n=0;n<y.Length;n++) y[n]=y[n]*.72f+f[n]*.28f; }
        if (feline>.0001f && y.Length>32) { var body=MovingAverage(y,5); var res=new float[y.Length]; DelayAdd(res,body,3.2f,.34f); DelayAdd(res,body,5.7f,.24f); DelayAdd(res,body,8.9f,-.12f); for(int n=0;n<y.Length;n++) y[n]+=res[n]*(.44f*feline); }
        if (growl>.0001f && y.Length>16) { var body=MovingAverage(y,9); for(int n=0;n<y.Length;n++) y[n]+=MathF.Tanh(body[n]*(3.2f+growl*6f))*(.06f+.16f*growl); }
        if (purrMix>.0001f && purrRate>1f && y.Length>32) { var body=MovingAverage(y,7); var env=NormalizeEnvelope(MovingAverage(Abs(y),Math.Max(9,(int)(KokoroRate*.012f)))); for(int n=0;n<y.Length;n++){ double t=n/(double)KokoroRate; float mod=(float)(.70*Math.Sin(2*Math.PI*purrRate*t)+.30*Math.Sin(2*Math.PI*(purrRate*.51)*t+.7)); y[n]+=body[n]*mod*env[n]*(.72f*purrMix);} }
        if (breath>.0001f && y.Length>8) { var smooth=MovingAverage(y,17); for(int n=0;n<y.Length;n++) y[n]+=MathF.Tanh((y[n]-smooth[n])*2.5f)*(.10f*breath); }
        if (spectral>.0001f && y.Length>32) { var up=PitchShift(y,.18f+spectral*.34f); var down=PitchShift(y,-.16f-spectral*.30f); var layer=new float[y.Length]; DelayLayer(layer,up,10f+spectral*7f,.085f+spectral*.075f); DelayLayer(layer,down,24f+spectral*13f,.070f+spectral*.070f); var orig=(float[])layer.Clone(); for(int n=1;n<layer.Length;n++) layer[n]+= (orig[n]-.90f*orig[n-1])*(.14f*spectral); for(int n=0;n<y.Length;n++) y[n]+=layer[n]*spectral; }
        if (reverb>.0001f && y.Length>32) { var dry=(float[])y.Clone(); var wet=new float[y.Length]; DelayLayer(wet,dry,44,.24f); DelayLayer(wet,dry,91,.18f); DelayLayer(wet,dry,164,.125f); DelayLayer(wet,dry,278,.080f); DelayLayer(wet,dry,431,.050f); var tail=(float[])wet.Clone(); int d=(int)Math.Round(KokoroRate*.137); if(d>0&&d<tail.Length) for(int n=d;n<tail.Length;n++) tail[n]+=wet[n-d]*.34f; for(int n=0;n<y.Length;n++) y[n]=dry[n]+tail[n]*(.78f*reverb); }
        return NormalizePeak(y);
    }

    static float[] PitchShift(float[] audio,float semis){ if(audio.Length==0||Math.Abs(semis)<.001f)return (float[])audio.Clone(); double ratio=Math.Pow(2,semis/12.0); int size=Math.Max(1,(int)Math.Round(audio.Length/ratio)); return ResampleToLength(audio,size); }
    static float[] Resample(float[] a,int oldRate,int newRate)=>oldRate==newRate?(float[])a.Clone():ResampleToLength(a,Math.Max(1,(int)Math.Round(a.Length*(double)newRate/oldRate)));
    static float[] ResampleToLength(float[] a,int len){ var o=new float[len]; if(a.Length==0)return o; if(len==1){o[0]=a[0];return o;} double scale=(a.Length-1d)/(len-1d); for(int n=0;n<len;n++){ double p=n*scale; int i=(int)p; int j=Math.Min(i+1,a.Length-1); float f=(float)(p-i); o[n]=a[i]+(a[j]-a[i])*f;} return o; }
    static float[] MovingAverage(float[] a,int taps){ taps=Math.Max(1,taps); if(taps<=1||a.Length<taps)return(float[])a.Clone(); var o=new float[a.Length]; int half=taps/2; for(int i=0;i<a.Length;i++){ double s=0; int c=0; for(int j=Math.Max(0,i-half);j<=Math.Min(a.Length-1,i+(taps-half-1));j++){s+=a[j];c++;} o[i]=(float)(s/c);} return o; }
    static float[] Fit(float[] a,int size){ var o=new float[size]; Array.Copy(a,o,Math.Min(a.Length,size)); return o; }
    static float[] Abs(float[] a){var o=new float[a.Length];for(int i=0;i<a.Length;i++)o[i]=MathF.Abs(a[i]);return o;}
    static float[] NormalizeEnvelope(float[] a){float m=1e-5f;foreach(var v in a)m=Math.Max(m,MathF.Abs(v));for(int i=0;i<a.Length;i++)a[i]/=m;return a;}
    static void DelayAdd(float[] dst,float[] src,float ms,float gain)=>DelayLayer(dst,src,ms,gain);
    static void DelayLayer(float[] dst,float[] src,float ms,float gain){int d=(int)Math.Round(KokoroRate*ms/1000f);if(d<=0||d>=dst.Length)return;int n=Math.Min(src.Length,dst.Length-d);for(int i=0;i<n;i++)dst[i+d]+=src[i]*gain;}
    static float[] Flutter(float[] a,float depth,float rate){var o=new float[a.Length]; double amp=1+Math.Min(8,depth*34),baseDelay=amp+1.5;for(int n=0;n<a.Length;n++){double t=n/(double)KokoroRate;double src=n-(baseDelay+amp*Math.Sin(2*Math.PI*rate*t));src=Math.Clamp(src,0,a.Length-1);int i=(int)src,j=Math.Min(i+1,a.Length-1);float f=(float)(src-i);o[n]=a[i]+(a[j]-a[i])*f;}return o;}
    static float[] NormalizePeak(float[] a,float peak=.98f){float m=0;foreach(float v in a)m=Math.Max(m,MathF.Abs(v));float scale=m>peak&&m>1e-8f?peak/m:1f;var o=new float[a.Length];for(int i=0;i<a.Length;i++)o[i]=Math.Clamp(a[i]*scale,-1f,1f);return o;}
    static float Clamp01(float v)=>Math.Clamp(v,0f,1f);
    static float Lerp(float a,float b,float t)=>a+(b-a)*t;
}
