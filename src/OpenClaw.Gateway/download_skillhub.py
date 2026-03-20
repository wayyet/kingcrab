import urllib.request
url = 'https://skillhub-1388575217.cos.ap-guangzhou.myqcloud.com/install/skillhub.md'
urllib.request.urlretrieve(url, 'skillhub.md')
print("Downloaded successfully")
