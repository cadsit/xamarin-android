#include "inmemory-assemblies.hh"
#include "globals.hh"

#include <mono/metadata/appdomain.h>
#include <mono/metadata/assembly.h>
#include <mono/metadata/reflection.h>

extern "C" {
#include "java-interop-util.h"
}

namespace xamarin::android::internal {

void
InMemoryAssemblies::add_or_update_from_java (MonoDomain *domain, JNIEnv *env, jstring_array_wrapper &assemblies, jobjectArray assembliesBytes)
{
	if (assembliesBytes == nullptr)
		return;

	unsigned int len = static_cast<unsigned int> (env->GetArrayLength (assembliesBytes));
	char **names = new char*[len];
	char **assemblies_bytes = new char*[len];
	unsigned int *assemblies_bytes_len = new unsigned int[len];

	for (unsigned int index = 0; index < len; index++) {
		jboolean is_copy;
		jbyteArray assembly_byte_array = reinterpret_cast <jbyteArray> (env->GetObjectArrayElement (assembliesBytes, (jsize)index));
		unsigned int bytes_len = static_cast<unsigned int> (env->GetArrayLength (assembly_byte_array));
		jbyte *bytes = env->GetByteArrayElements (assembly_byte_array, &is_copy);
		jstring_wrapper &assembly = assemblies [index];

		names[index] = utils.strdup_new (assembly.get_cstr ());
		assemblies_bytes_len[index] = bytes_len;
		assemblies_bytes[index] = new char[bytes_len];
		memcpy ((void*)assemblies_bytes[index], bytes, bytes_len);

		env->ReleaseByteArrayElements (assembly_byte_array, bytes, JNI_ABORT);
	}

	InMemoryAssemblyEntry *new_entry = new InMemoryAssemblyEntry ();
	new_entry->domain_id = mono_domain_get_id (domain);
	new_entry->assemblies_count = len;
	new_entry->names = names;
	new_entry->assemblies_bytes = assemblies_bytes;
	new_entry->assemblies_bytes_len = assemblies_bytes_len;

	add_or_replace_entry (new_entry);
}

MonoAssembly*
InMemoryAssemblies::load_assembly_from_memory (MonoDomain *domain, MonoAssemblyName *name)
{
	int domain_id = mono_domain_get_id (domain);
	InMemoryAssemblyEntry *entry = find_entry (domain_id);
	if (entry == nullptr)
		return nullptr;

	const char *asm_name = mono_assembly_name_get_name (name);
	unsigned int asm_count = entry->assemblies_count;

	for (unsigned int i = 0; i < asm_count; i++) {
		const char *entry_name = entry->names[i];
		const char *entry_bytes = entry->assemblies_bytes[i];
		const unsigned int entry_bytes_len = entry->assemblies_bytes_len[i];

		if (strcmp (asm_name, entry_name) != 0)
			continue;

		// There is unfortunately no public unmanaged API to do proper in-memory
		// loading (it would require access to the MonoAssemblyLoadRequest API)
		MonoClass *assembly_klass = utils.monodroid_get_class_from_name (domain, "mscorlib", "System.Reflection", "Assembly");
		MonoClass *byte_klass = mono_get_byte_class ();
		// Use the variant with 3 parameters so that we always get the first argument being a byte[]
		// (the two last don't matter since we pass null anyway)
		MonoMethod *assembly_load_method = mono_class_get_method_from_name (assembly_klass, "Load", 3);
		MonoArray *byteArray = mono_array_new (domain, byte_klass, entry_bytes_len);
		mono_value_copy_array (byteArray, 0, (void*)entry_bytes, (int)entry_bytes_len);

		void *args[3];
		args[0] = byteArray;
		args[1] = nullptr;
		args[2] = nullptr;
		MonoReflectionAssembly *res = (MonoReflectionAssembly *)utils.monodroid_runtime_invoke (domain, assembly_load_method, nullptr, args, nullptr);
		MonoAssembly *mono_assembly = mono_reflection_assembly_get_assembly (res);

		return mono_assembly;
	}

	return nullptr;
}

void
InMemoryAssemblies::clear_for_domain (MonoDomain *domain)
{
	int domain_id = mono_domain_get_id (domain);
	InMemoryAssemblyEntry *entry = remove_entry (domain_id);
	delete entry;
}

InMemoryAssemblies::InMemoryAssemblyEntry*
InMemoryAssemblies::find_entry (int domain_id)
{
	for (unsigned int i = 0; i < length; i++) {
		auto entry = entries[i];
		if (entry->domain_id == domain_id)
			return entry;
	}
	return nullptr;
}

void
InMemoryAssemblies::add_or_replace_entry (InMemoryAssemblies::InMemoryAssemblyEntry *new_entry)
{
	for (unsigned int i = 0; i < length; i++) {
		auto entry = entries[i];
		if (entry->domain_id == new_entry->domain_id) {
			entries[i] = new_entry;
			delete entry;
			return;
		}
	}
	add_entry (new_entry);
}

void
InMemoryAssemblies::add_entry (InMemoryAssemblies::InMemoryAssemblyEntry *entry)
{
	if (length >= capacity) {
		capacity = MULTIPLY_WITH_OVERFLOW_CHECK(unsigned int, capacity, 2);
		InMemoryAssemblyEntry **new_entries = new InMemoryAssemblyEntry*[capacity];
		memcpy ((void*)new_entries, entries, MULTIPLY_WITH_OVERFLOW_CHECK(size_t, sizeof(void*), length));
		InMemoryAssemblyEntry **old_entries = entries;
		entries = new_entries;
		delete[] old_entries;
	}
	entries[length++] = entry;
}

InMemoryAssemblies::InMemoryAssemblyEntry*
InMemoryAssemblies::remove_entry (int domain_id)
{
	for (unsigned int i = 0; i < length; i++) {
		InMemoryAssemblyEntry *entry = entries[i];
		if (entry->domain_id == domain_id) {
			for (unsigned int j = i; j < length - 1; j++)
				entries[j] = entries[j + 1];
			length--;
			return entry;
		}
	}
	return nullptr;
}

}