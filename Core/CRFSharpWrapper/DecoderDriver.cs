﻿/**********************************************/
/*Project: CRF#                               */
/*Author: Zhongkai Fu                         */
/*Email: fuzhongkai@gmail.com                 */
/**********************************************/

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AdvUtils;
using CRFSharp;

namespace CRFSharpWrapper
{
    public class DecoderDriver
    {
        readonly object rdLocker = new object();

        public bool Decode(CRFSharpWrapper.DecoderArgs options)
        {
            var parallelOption = new ParallelOptions();
            var watch = Stopwatch.StartNew();
            if (File.Exists(options.strInputFileName) == false)
            {
                Logger.WriteLine("FAILED: Open {0} file failed.", options.strInputFileName);
                return false;
            }

            if (File.Exists(options.strModelFileName) == false)
            {
                Logger.WriteLine("FAILED: Open {0} file failed.", options.strModelFileName);
                return false;
            }

            var sr = new StreamReader(options.strInputFileName);
            StreamWriter sw = null, swSeg = null;

            if (!string.IsNullOrEmpty(options.strOutputFileName))
            {
                sw = new StreamWriter(options.strOutputFileName);
            }
            if (!string.IsNullOrEmpty(options.strOutputSegFileName))
            {
                swSeg = new StreamWriter(options.strOutputSegFileName);
            }

            //Create CRFSharp wrapper instance. It's a global instance
            var crfWrapper = new CRFSharpWrapper.Decoder();

            //Load encoded model from file
            Logger.WriteLine("Loading model from {0}", options.strModelFileName);
            crfWrapper.LoadModel(options.strModelFileName);

            var queueRecords = new ConcurrentQueue<List<List<string>>>();
            var queueSegRecords = new ConcurrentQueue<List<List<string>>>();

            parallelOption.MaxDegreeOfParallelism = options.thread;
            Parallel.For(0, options.thread, parallelOption, t =>
            {

                //Create decoder tagger instance. If the running environment is multi-threads, each thread needs a separated instance
                var tagger = crfWrapper.CreateTagger(options.nBest, options.maxword);
                tagger.set_vlevel(options.probLevel);

                //Initialize result
                var crf_out = new crf_seg_out[options.nBest];
                for (var i = 0; i < options.nBest; i++)
                {
                    crf_out[i] = new crf_seg_out(tagger.crf_max_word_num);
                }

                var inbuf = new List<List<string>>();
                while (true)
                {
                    lock (rdLocker)
                    {
                        if (ReadRecord(inbuf, sr) == false)
                        {
                            break;
                        }

                        queueRecords.Enqueue(inbuf);
                        queueSegRecords.Enqueue(inbuf);
                    }

                    //Call CRFSharp wrapper to predict given string's tags
                    if (swSeg != null)
                    {
                        crfWrapper.Segment(crf_out, tagger, inbuf);
                    }
                    else
                    {
                        crfWrapper.Segment(crf_out, (DecoderTagger)tagger, inbuf);
                    }

                    List<List<string>> peek = null;
                    //Save segmented tagged result into file
                    if (swSeg != null)
                    {
                        List<string> rstList = ConvertCRFTermOutToStringList(inbuf, crf_out);
                        while (peek != inbuf)
                        {
                            queueSegRecords.TryPeek(out peek);
                        }
                        foreach (var item in rstList)
                        {
                            swSeg.WriteLine(item);
                        }
                        queueSegRecords.TryDequeue(out peek);
                        peek = null;
                    }

                    //Save raw tagged result (with probability) into file
                    if (sw != null)
                    {
                        while (peek != inbuf)
                        {
                            queueRecords.TryPeek(out peek);
                        }
                        OutputRawResultToFile(inbuf, crf_out, tagger, sw);
                        queueRecords.TryDequeue(out peek);

                    }
                }
            });


            sr.Close();

            sw?.Close();
            swSeg?.Close();
            watch.Stop();
            Logger.WriteLine("Elapsed: {0} ms", watch.ElapsedMilliseconds);

            return true;
        }

        private bool ReadRecord(List<List<string>> inbuf, StreamReader sr)
        {
            inbuf.Clear();

            while (true)
            {
                var strLine = sr.ReadLine();
                if (strLine == null)
                {
                    //At the end of current file
                    return inbuf.Count != 0;
                }
                strLine = strLine.Trim();
                if (strLine.Length == 0)
                {
                    return true;
                }

                //Read feature set for each record
                var items = strLine.Split('\t');
                inbuf.Add(new List<string>());
                foreach (var item in items)
                {
                    inbuf[inbuf.Count - 1].Add(item);
                }
            }
        }

        //Output raw result with probability
        private void OutputRawResultToFile(List<List<string>> inbuf, crf_term_out[] crf_out, SegDecoderTagger tagger, StreamWriter sw)
        {
            foreach (crf_term_out crf_seg_out in crf_out)
            {
                if (crf_seg_out == null)
                {
                    //No more result
                    break;
                }

                var sb = new StringBuilder();

                //Show the entire sequence probability
                //For each token
                for (var i = 0; i < inbuf.Count; i++)
                {
                    //Show all features
                    for (var j = 0; j < inbuf[i].Count; j++)
                    {
                        sb.Append(inbuf[i][j]);
                        sb.Append("\t");
                    }

                    //Show the best result and its probability
                    sb.Append(crf_seg_out.result_[i]);

                    if (tagger.vlevel_ > 1)
                    {
                        sb.Append("\t");
                        sb.Append(crf_seg_out.weight_[i]);

                        //Show the probability of all tags
                        sb.Append("\t");
                        for (var j = 0; j < tagger.ysize_; j++)
                        {
                            sb.Append(tagger.yname(j));
                            sb.Append("/");
                            sb.Append(tagger.prob(i, j));

                            if (j < tagger.ysize_ - 1)
                            {
                                sb.Append("\t");
                            }
                        }
                    }
                    sb.AppendLine();
                }
                if (tagger.vlevel_ > 0)
                {
                    sw.WriteLine("#{0}", crf_seg_out.prob);
                }
                sw.WriteLine(sb.ToString().Trim());
                sw.WriteLine();
            }
        }

        //Convert CRFSharp output format to string list
        private List<string> ConvertCRFTermOutToStringList(List<List<string>> inbuf, crf_seg_out[] crf_out)
        {
            var sb = new StringBuilder();
            foreach (List<string> list in inbuf)
            {
                sb.Append(list[0]);
            }

            var strText = sb.ToString();
            var rstList = new List<string>();
            foreach (crf_seg_out crf_term_out in crf_out)
            {
                if (crf_term_out == null)
                {
                    //No more result
                    break;
                }

                sb.Clear();
                for (var j = 0; j < crf_term_out.Count; j++)
                {
                    var str = strText.Substring(crf_term_out.tokenList[j].offset, crf_term_out.tokenList[j].length);
                    var strNE = crf_term_out.tokenList[j].strTag;

                    sb.Append(str);
                    if (strNE.Length > 0)
                    {
                        sb.Append("[" + strNE + "]");
                    }
                    sb.Append(" ");
                }
                rstList.Add(sb.ToString().Trim());
            }

            return rstList;
        }
    }
}