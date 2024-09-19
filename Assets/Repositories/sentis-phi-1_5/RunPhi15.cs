using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Sentis;
using System.IO;
using System.Text;
using FF = Unity.Sentis.Functional;

public class RunPhi15 : MonoBehaviour
{
    // ���� ���� �鿣�� Ÿ�� ����
    const BackendType backend = BackendType.GPUCompute;

    // ���� ���ڿ�
    string outputString = "One day an alien came down from Mars. It saw a chicken";

    // �ִ� ��ū ��
    const int maxTokens = 100;

    // ���������� �����ϴ� ��, ���� �������� �� ��������
    const float predictability = 5f;

    // �ؽ�Ʈ ���Ḧ ��Ÿ���� Ư���� ��ū
    const int END_OF_TEXT = 50256;

    // ���ָ� ������ �迭
    string[] tokens;

    // Sentis ���� �۾���
    IWorker engine;

    // ���� ������ ��ū �ε���
    int currentToken = 0;

    // ������ ��ū�� ������ �迭
    int[] outputTokens = new int[maxTokens];

    // ������ �� ��ū ��
    int totalTokens = 0;

    // ���ֺ��� ��Ģ�� �������� ������ �迭 �� ��ųʸ�
    string[] merges;
    Dictionary<string, int> vocab;

    // ������ �ؽ�Ʈ�� ����� �� ���ߵ��� ������ ��ū ��
    const int stopAfter = 100;

    // Ư�� ���� ó���� ���� �迭
    int[] whiteSpaceCharacters = new int[256];
    int[] encodedCharacters = new int[256];

    void Start()
    {
        SetupWhiteSpaceShifts();
        LoadVocabulary();

        var model1 = ModelLoader.Load(Path.Join(Application.streamingAssetsPath, "phi15.sentis"));
        int outputIndex = model1.outputs.Count - 1;

        var model2 = FF.Compile(
            (input, currentToken) =>
            {
                var row = FF.Select(model1.Forward(input)[outputIndex], 1, currentToken);
                return FF.Multinomial(predictability * row, 1);
            },
            (model1.inputs[0], InputDef.Int(new TensorShape()))
        );

        engine = WorkerFactory.CreateWorker(backend, model2);
    }

    // �ؽ�Ʈ ������ ó���ϴ� �ڷ�ƾ
    public IEnumerator GenerateText(string inputText, System.Action<string> callback)
    {
        DecodePrompt(inputText); // �Էµ� �ؽ�Ʈ�� ������Ʈ�� ���

        bool runInference = true;
        string generatedText = ""; // ���� ������ ����� ���ο� �ؽ�Ʈ�� ��� ���� ����
        currentToken = 0;
        totalTokens = 0;

        while (runInference)
        {
            using var tokensSoFar = new TensorInt(new TensorShape(1, maxTokens), outputTokens);
            using var index = new TensorInt(currentToken);

            engine.Execute(new Dictionary<string, Tensor> { { "input_0", tokensSoFar }, { "input_1", index } });

            var probs = engine.PeekOutput() as TensorInt;
            probs.CompleteOperationsAndDownload();

            int ID = probs[0];

            if (currentToken >= maxTokens - 1)
            {
                for (int i = 0; i < maxTokens - 1; i++) outputTokens[i] = outputTokens[i + 1];
                currentToken--;
            }

            outputTokens[++currentToken] = ID;
            totalTokens++;

            if (ID == END_OF_TEXT || totalTokens >= stopAfter)
            {
                runInference = false;
            }
            else if (ID < 0 || ID >= tokens.Length)
            {
                generatedText = " ";  // ��ū�� ������ ��� ��� ���� �߰�
            }
            else
            {
                generatedText = GetUnicodeText(tokens[ID]);  // ���ο� �ؽ�Ʈ�� ���
            }

            // �ݹ��� ���� ������ �ؽ�Ʈ ��ȯ
            callback?.Invoke(generatedText);

            yield return null;
        }

        Debug.Log("Text Generation Completed");
    }

    // ������Ʈ�� ���ڵ��ϰ� ��ū���� ��ȯ
    void DecodePrompt(string text)
    {
        var inputTokens = GetTokens(text);

        for (int i = 0; i < inputTokens.Count; i++)
        {
            outputTokens[i] = inputTokens[i];
        }
        currentToken = inputTokens.Count - 1;
    }

    // ���� �����͸� �ε�
    void LoadVocabulary()
    {
        var jsonText = File.ReadAllText(Path.Join(Application.streamingAssetsPath, "vocab_phi.json"));
        vocab = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, int>>(jsonText);
        tokens = new string[vocab.Count];
        foreach (var item in vocab)
        {
            tokens[item.Value] = item.Key;
        }

        merges = File.ReadAllLines(Path.Join(Application.streamingAssetsPath, "merges.txt"));
    }

    // �����ڵ� �ؽ�Ʈ�� ��ȯ
    string GetUnicodeText(string text)
    {
        var bytes = Encoding.GetEncoding("ISO-8859-1").GetBytes(ShiftCharacterDown(text));
        return Encoding.UTF8.GetString(bytes);
    }

    // ASCII �ؽ�Ʈ�� ��ȯ
    string GetASCIIText(string newText)
    {
        var bytes = Encoding.UTF8.GetBytes(newText);
        return ShiftCharacterUp(Encoding.GetEncoding("ISO-8859-1").GetString(bytes));
    }

    // Ư�� ���ڸ� ��ȯ�ϱ� ���� �Լ�
    string ShiftCharacterDown(string text)
    {
        string outText = "";
        foreach (char letter in text)
        {
            outText += ((int)letter <= 256) ? letter : (char)whiteSpaceCharacters[(int)(letter - 256)];
        }
        return outText;
    }

    string ShiftCharacterUp(string text)
    {
        string outText = "";
        foreach (char letter in text)
        {
            outText += (char)encodedCharacters[(int)letter];
        }
        return outText;
    }

    // ȭ��Ʈ�����̽� ó���� ���� ����
    void SetupWhiteSpaceShifts()
    {
        for (int i = 0, n = 0; i < 256; i++)
        {
            encodedCharacters[i] = i;
            if (IsWhiteSpace(i))
            {
                encodedCharacters[i] = n + 256;
                whiteSpaceCharacters[n++] = i;
            }
        }
    }

    // ȭ��Ʈ�����̽� ���� Ȯ��
    bool IsWhiteSpace(int i)
    {
        return i <= 32 || (i >= 127 && i <= 160) || i == 173;
    }

    // �ؽ�Ʈ�� ��ū ����Ʈ�� ��ȯ
    List<int> GetTokens(string text)
    {
        text = GetASCIIText(text);

        var inputTokens = new List<string>();
        foreach (var letter in text)
        {
            inputTokens.Add(letter.ToString());
        }

        ApplyMerges(inputTokens);

        var ids = new List<int>();
        foreach (var token in inputTokens)
        {
            if (vocab.TryGetValue(token, out int id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    // ���� ���� ��Ģ ����
    void ApplyMerges(List<string> inputTokens)
    {
        foreach (var merge in merges)
        {
            string[] pair = merge.Split(' ');
            int n = 0;
            while (n >= 0)
            {
                n = inputTokens.IndexOf(pair[0], n);
                if (n != -1 && n < inputTokens.Count - 1 && inputTokens[n + 1] == pair[1])
                {
                    inputTokens[n] += inputTokens[n + 1];
                    inputTokens.RemoveAt(n + 1);
                }
                if (n != -1) n++;
            }
        }
    }

    // ���� ����
    private void OnDestroy()
    {
        engine?.Dispose();
    }
}
