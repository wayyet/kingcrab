import urllib.request
url = 'https://skillhub-1388575217.cos.ap-guangzhou.myqcloud.com/install/install.sh'
urllib.request.urlretrieve(url, 'install.sh')
print("Downloaded install.sh successfully")
