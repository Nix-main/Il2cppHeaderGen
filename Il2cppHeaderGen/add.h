template <typename T>
struct ReferenceArray {
    Il2CppObject obj;
    Il2CppArrayBounds *bounds;
    il2cpp_array_size_t max_length;
    T* m_Items[65535];
};

template <typename T>
struct ValueArray {
    Il2CppObject obj;
    Il2CppArrayBounds* bounds;
    il2cpp_array_size_t max_length;
    T* m_Items[65535];
};

template <typename T>
struct ReferenceList {
    ReferenceList_c<T> *klass;
    void *monitor;
    struct ReferenceArray<T>* _items;
    int32_t _size;
    int32_t _version;
    Il2CppObject* _syncRoot;
};

template <typename T>
struct ReferenceList_c {
    char pad[184];
    struct ReferenceList_StaticFields<T>* static_fields;
};

template <typename T>
struct ReferenceList_StaticFields {
    struct ReferenceArray<T>* s_emptyArray;
};

template <typename T>
struct ValueList {
    ValueList_c<T> *klass;
    void *monitor;
    struct ValueArray<T>* _items;
    int32_t _size;
    int32_t _version;
    Il2CppObject* _syncRoot;
};

template <typename T>
struct ValueList_c {
    char pad[184];
    struct ValueList_StaticFields<T>* static_fields;
};

template <typename T>
struct ValueList_StaticFields {
    struct ValueArray<T>* s_emptyArray;
};

template <typename T, typename V>
struct Dictionary_Entry_Fields {
    int32_t hashCode;
    int32_t next;
    Il2CppObject* key;
    Il2CppObject* value;
};

template <typename T, typename V>
struct Dictionary_Entry {
    Dictionary_Entry_Fields<T, V> fields;
};

template <typename T, typename V>
struct Dictionary {
    void* klass;
    void* monitor;
    struct ValueArray<System_Int32>* _buckets;
    struct ValueArray<Dictionary_Entry<T, V>>* _entries;
    int32_t _count;
    int32_t _freeList;
    int32_t _freeCount;
    int32_t _version;
    struct System_Collections_Generic_IEqualityComparer_TKey__o* _comparer;
    struct System_Collections_Generic_Dictionary_KeyCollection_TKey__TValue__o* _keys;
    struct System_Collections_Generic_Dictionary_ValueCollection_TKey__TValue__o* _values;
    Il2CppObject* _syncRoot;
};